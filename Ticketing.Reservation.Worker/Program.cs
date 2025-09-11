using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using StackExchange.Redis;
using Ticketing.Shared;

var builder = Host.CreateApplicationBuilder(args);

// ---- Configuration ----
var rabbitUri = builder.Configuration["RABBITMQ__URI"]
    ?? "amqp://admin:Admin123!@localhost:5672/";
var redisConn = builder.Configuration["REDIS__CONNECTION"]
    ?? "localhost:6379,password=DevStrongPassword_ChangeMe,ssl=False,abortConnect=False";
var holdTtlSeconds = int.TryParse(builder.Configuration["HOLD_TTL_SECONDS"], out var ttl)
    ? ttl : 300;

// JSON options (string enums; ignore nulls)
var jsonOptions = new JsonSerializerOptions
{
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};
jsonOptions.Converters.Add(new JsonStringEnumConverter());

// ---- Services ----
builder.Services.AddSingleton(jsonOptions);

builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConn));

builder.Services.AddSingleton<IConnection>(_ =>
{
    var cf = new ConnectionFactory
    {
        Uri = new Uri(rabbitUri),
        DispatchConsumersAsync = true // worker will consume asynchronously
    };
    return cf.CreateConnection("reservation-worker");
});

builder.Services.AddSingleton<IModel>(sp =>
{
    var conn = sp.GetRequiredService<IConnection>();
    var ch = conn.CreateModel();
    ch.QueueDeclare(queue: Queues.Reservations, durable: true, exclusive: false, autoDelete: false, arguments: null);
    ch.BasicQos(0, 50, false); // fair dispatch
    return ch;
});

builder.Services.AddHostedService<ReservationConsumer>();
builder.Services.AddHostedService<HoldReclaimer>();   // Reclaim HOLD expired tickets

var host = builder.Build();
await host.RunAsync();

sealed class ReservationConsumer : BackgroundService
{
    private readonly IModel _channel;
    private readonly IDatabase _db;
    private readonly JsonSerializerOptions _json;
    private readonly int _holdTtlSeconds;

    public ReservationConsumer(IModel channel, IConnectionMultiplexer redis, JsonSerializerOptions json, IConfiguration cfg)
    {
        _channel = channel;
        _db = redis.GetDatabase();
        _json = json;
        _holdTtlSeconds = int.TryParse(cfg["HOLD_TTL_SECONDS"], out var t) ? t : 300;
    }

    // Atomically:
    // - if stock >= qty: DECRBY stock, create hold hash, set hold TTL (key expiry), write status: Reserved
    // - else: write status: SoldOut
    // Atomically:
    // - if stock >= qty: DECRBY stock, create hold hash, INDEX it in ZSET by expiry, write Reserved status
    // - else: write SoldOut
    private const string ReserveLua = @"
local stockKey     = KEYS[1]
local holdKey      = KEYS[2]
local holdsIndex   = KEYS[3]   -- ZSET per event: member=trackingId, score=expireAt
local statusKey    = KEYS[4]

local qty          = tonumber(ARGV[1])
local now          = tonumber(ARGV[2])
local ttl          = tonumber(ARGV[3])
local eventId      = ARGV[4]
local trackingId   = ARGV[5]
local reservedJson = ARGV[6]
local soldoutJson  = ARGV[7]

local stock = tonumber(redis.call('get', stockKey) or '0')
if stock >= qty then
  redis.call('decrby', stockKey, qty)
  redis.call('hset', holdKey, 'eventId', eventId, 'qty', qty, 'expiresAt', now + ttl)
  -- no TTL on hold hash; reclaimer will delete it after processing
  redis.call('zadd', holdsIndex, now + ttl, trackingId)  -- index by expiry
  redis.call('set', statusKey, reservedJson)
  redis.call('expire', statusKey, ttl + 60)
  return 'RESERVED'
else
  redis.call('set', statusKey, soldoutJson)
  redis.call('expire', statusKey, 120)
  return 'SOLD_OUT'
end
";


    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.Received += OnMessageAsync;
        _channel.BasicConsume(Queues.Reservations, autoAck: false, consumer: consumer);
        return Task.CompletedTask;
    }

    private async Task OnMessageAsync(object sender, BasicDeliverEventArgs ea)
    {
        try
        {
            var msg = JsonSerializer.Deserialize<ReservationMessage>(ea.Body.Span, _json)!;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var stockKey = $"stock:{msg.EventId}";
            var holdKey = $"hold:{msg.TrackingId}";
            var indexKey = $"holds:{msg.EventId}";     // NEW
            var statusKey = $"status:{msg.TrackingId}";

            var reservedStatus = new ReservationStatus(
                TrackingId: msg.TrackingId,
                Status: ReservationStatusKind.Reserved,
                ExpiresAtEpoch: now + _holdTtlSeconds,
                Quantity: msg.Quantity,
                EventId: msg.EventId,
                Message: "Hold placed"
            );
            var soldoutStatus = new ReservationStatus(
                TrackingId: msg.TrackingId,
                Status: ReservationStatusKind.SoldOut,
                Message: "Insufficient stock"
            );

            var result = (string)await _db.ScriptEvaluateAsync(
                ReserveLua,
                keys: new RedisKey[] { stockKey, holdKey, indexKey, statusKey },   // UPDATED
                values: new RedisValue[]
                {
                    msg.Quantity,
                    now,
                    _holdTtlSeconds,
                    msg.EventId,
                    msg.TrackingId,
                    JsonSerializer.Serialize(reservedStatus, _json),
                    JsonSerializer.Serialize(soldoutStatus, _json)
                });

            // Ack regardless; if we want retries only for transient errors, we'd add more logic here.
            _channel.BasicAck(ea.DeliveryTag, multiple: false);
        }
        catch (Exception)
        {
            // Basic retry by requeue; in real prod, route to DLQ after N retries.
            _channel.BasicNack(ea.DeliveryTag, multiple: false, requeue: true);
        }
    }
}


sealed class HoldReclaimer : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly JsonSerializerOptions _json;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(3);

    public HoldReclaimer(IConnectionMultiplexer redis, JsonSerializerOptions json)
    {
        _redis = redis;
        _json = json;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var db = _redis.GetDatabase();
        var endpoints = _redis.GetEndPoints();
        if (endpoints.Length == 0) throw new InvalidOperationException("No Redis endpoints found.");
        var server = _redis.GetServer(endpoints[0]);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Discover active indexes. In prod, track eventIds in a set instead of scanning.
                var indexKeys = server.Keys(pattern: "holds:*", pageSize: 100).ToArray();
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                foreach (var indexKey in indexKeys)
                {
                    // Up to 200 expired holds per tick
                    var expired = await db.SortedSetRangeByScoreAsync(
                        indexKey, double.NegativeInfinity, now, Exclude.None, Order.Ascending, 0, 200);

                    if (expired.Length == 0) continue;

                    foreach (var tracking in expired)
                    {
                        var trackingId = (string)tracking!;
                        var holdKey = $"hold:{trackingId}";
                        var fields = await db.HashGetAsync(holdKey, new RedisValue[] { "qty", "eventId" });

                        // If missing, drop the index member.
                        if (fields.Length != 2 || !fields[0].HasValue || !fields[1].HasValue)
                        {
                            await db.SortedSetRemoveAsync(indexKey, tracking);
                            continue;
                        }

                        var qty = (int)fields[0];
                        var eventId = (string)fields[1]!;

                        // Return stock
                        await db.StringIncrementAsync($"stock:{eventId}", qty);

                        // Clean up
                        await db.KeyDeleteAsync(holdKey);
                        await db.SortedSetRemoveAsync(indexKey, tracking);

                        // Mark status as Expired
                        var statusKey = $"status:{trackingId}";
                        var expiredStatus = new ReservationStatus(
                            TrackingId: trackingId,
                            Status: ReservationStatusKind.Expired,
                            Message: "Hold expired and stock returned",
                            EventId: eventId,
                            Quantity: qty
                        );
                        await db.StringSetAsync(statusKey,
                            JsonSerializer.Serialize(expiredStatus, _json),
                            TimeSpan.FromMinutes(5));
                    }
                }
            }
            catch
            {
                // add logging in prod; continue loop
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }
}

