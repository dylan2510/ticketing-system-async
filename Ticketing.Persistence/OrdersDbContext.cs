using Microsoft.EntityFrameworkCore;

namespace Ticketing.Persistence;

public class OrdersDbContext : DbContext
{
    public OrdersDbContext(DbContextOptions<OrdersDbContext> options) : base(options) { }

    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Order>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TrackingId).IsUnique();
            e.Property(x => x.TrackingId).IsRequired();
            e.Property(x => x.EventId).IsRequired();
            e.Property(x => x.Quantity).IsRequired();
            e.Property(x => x.CreatedAtUtc).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });
    }
}

public class Order
{
    public int Id { get; set; }
    public string TrackingId { get; set; } = default!;
    public string EventId { get; set; } = default!;
    public int Quantity { get; set; }
    public string? UserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
