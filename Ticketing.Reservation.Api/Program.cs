using RabbitMQ.Client;
using StackExchange.Redis;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Ticketing.Persistence;

var builder = WebApplication.CreateBuilder(args);

// Config
var rabbitUri = builder.Configuration["RABBITMQ__URI"]
    ?? "amqp://admin:Admin123!@localhost:5672/";
var redisConn = builder.Configuration["REDIS__CONNECTION"]
    ?? "localhost:6379,password=DevStrongPassword_ChangeMe,ssl=False,abortConnect=False";

// Services
builder.Services.AddControllers()
.AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConn));
builder.Services.AddSingleton<IConnection>(_ =>
{
    var cf = new ConnectionFactory { Uri = new(rabbitUri), DispatchConsumersAsync = true };
    return cf.CreateConnection("reservation-api");
});
builder.Services.AddSingleton<IModel>(sp =>
{
    var conn = sp.GetRequiredService<IConnection>();
    return conn.CreateModel(); // controllers will declare queues
});

// DB
var ordersCs = builder.Configuration.GetConnectionString("Orders") ?? "Data Source=orders.db";
builder.Services.AddDbContext<OrdersDbContext>(opt => opt.UseSqlite(ordersCs));

// (Optional CORS)
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

app.UseCors();
app.UseSwagger();
app.UseSwaggerUI();

app.MapControllers();

// Ensure DB exists (demo simplicity)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
    db.Database.EnsureCreated();
}

app.Run();

