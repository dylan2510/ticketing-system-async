using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using RabbitMQ.Client;
using StackExchange.Redis;
using Ticketing.Shared;
using System.Text.Json.Serialization;
using Ticketing.Persistence;
using Microsoft.EntityFrameworkCore;


namespace Ticketing.Reservation.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ReservationsController : ControllerBase
{
    private readonly IModel _channel;
    private readonly IConnectionMultiplexer _redis;

    private static readonly JsonSerializerOptions _json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public ReservationsController(IModel channel, IConnectionMultiplexer redis)
    {
        _channel = channel;
        _redis = redis;

        // Ensure queue exists (idempotent)
        _channel.QueueDeclare(queue: Queues.Reservations, durable: true, exclusive: false, autoDelete: false, arguments: null);
    }

    /// <summary>
    /// Create a reservation request (enqueue) and return a trackingId with 202 Accepted.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ReservationRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.EventId))
            return BadRequest("EventId required");
        if (req.Quantity <= 0 || req.Quantity > 10)
            return BadRequest("Quantity must be 1..10");

        var trackingId = Guid.NewGuid().ToString("N");

        var pending = new ReservationStatus(
            TrackingId: trackingId,
            Status: ReservationStatusKind.Pending,
            Message: "Queued for processing",
            EventId: req.EventId,
            Quantity: req.Quantity
        );

        var db = _redis.GetDatabase();
        await db.StringSetAsync($"status:{trackingId}", JsonSerializer.Serialize(pending, _json), expiry: TimeSpan.FromMinutes(6));

        var message = new ReservationMessage(trackingId, req.EventId, req.Quantity, req.UserId);
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        var props = _channel.CreateBasicProperties();
        props.Persistent = true;
        props.MessageId = trackingId;
        props.Type = "ReservationRequested";
        _channel.BasicPublish(exchange: "", routingKey: Queues.Reservations, basicProperties: props, body: body);

        return Accepted($"/api/reservations/{trackingId}", new ReservationAccepted(trackingId));
    }

    /// <summary>
    /// Poll the reservation status by trackingId.
    /// </summary>
    [HttpGet("{trackingId}")]
    public async Task<IActionResult> GetStatus([FromRoute] string trackingId)
    {
        if (string.IsNullOrWhiteSpace(trackingId))
            return BadRequest();

        var s = await _redis.GetDatabase().StringGetAsync($"status:{trackingId}");
        if (s.IsNullOrEmpty) return NotFound();

        var status = JsonSerializer.Deserialize<ReservationStatus>(s!, _json)!;
        return Ok(status);
    }
    
    /// <summary>
/// Confirm a reserved hold and persist an order (idempotent).
/// </summary>
[HttpPost("{trackingId}/confirm")]
public async Task<IActionResult> Confirm([FromRoute] string trackingId, [FromServices] OrdersDbContext dbCtx)
{
    if (string.IsNullOrWhiteSpace(trackingId)) return BadRequest();

    var db = _redis.GetDatabase();
    var holdKey = $"hold:{trackingId}";
    var holdFields = await db.HashGetAsync(holdKey, new RedisValue[] { "eventId", "qty", "expiresAt" });

    // Hold must exist
    if (holdFields.Length != 3 || !holdFields[0].HasValue || !holdFields[1].HasValue || !holdFields[2].HasValue)
        return Conflict(new { error = "HoldNotFoundOrExpired" });

    var eventId = (string)holdFields[0]!;
    var qty = (int)holdFields[1];
    var expiresAt = (long)holdFields[2];
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    if (now > expiresAt) return Conflict(new { error = "HoldExpired" });

    // Idempotency (avoid double confirm)
    var idempKey = $"idemp:confirm:{trackingId}";
    if (!await db.StringSetAsync(idempKey, "1", expiry: TimeSpan.FromMinutes(5), when: When.NotExists))
    {
        // someone else is/was confirming; proceed to return current status for idempotency
    }

    // Insert order if not exists (unique on TrackingId)
    var existing = await dbCtx.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.TrackingId == trackingId);
    if (existing is null)
    {
        var order = new Ticketing.Persistence.Order
        {
            TrackingId = trackingId,
            EventId = eventId,
            Quantity = qty,
            UserId = null // optional: you can pass userId via body/query if you want
        };
        dbCtx.Orders.Add(order);
        try
        {
            await dbCtx.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Unique constraint race: another request wrote it first; proceed
        }
    }

    // Cleanup Redis so the reclaimer won't reclaim stock
    await db.KeyDeleteAsync(holdKey);
    await db.SortedSetRemoveAsync($"holds:{eventId}", trackingId);

    // Mark status as Confirmed
    var statusKey = $"status:{trackingId}";
    var confirmed = new Ticketing.Shared.ReservationStatus(
        TrackingId: trackingId,
        Status: Ticketing.Shared.ReservationStatusKind.Confirmed,
        EventId: eventId,
        Quantity: qty,
        Message: "Order confirmed"
    );
    await db.StringSetAsync(statusKey, JsonSerializer.Serialize(confirmed, _json), TimeSpan.FromHours(1));

    return Ok(confirmed);
}

}
