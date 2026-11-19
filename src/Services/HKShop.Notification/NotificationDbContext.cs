using HKShop.Messaging;
using Microsoft.EntityFrameworkCore;

namespace HKShop.Notification;

public sealed class ConfirmationEmail
{
    public Guid OrderId { get; set; }
    public Guid EventId { get; set; }
    public string OrderNumber { get; set; } = "";
    public string Recipient { get; set; } = "";
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
}

public sealed class NotificationMessage
{
    public long Id { get; set; }
    public string EventKey { get; set; } = "";
    public string Recipient { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
}

public sealed class NotificationDbContext(DbContextOptions<NotificationDbContext> options) : DbContext(options)
{
    public DbSet<ConfirmationEmail> ConfirmationEmails => Set<ConfirmationEmail>();
    public DbSet<NotificationMessage> NotificationMessages => Set<NotificationMessage>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var email = model.Entity<ConfirmationEmail>();
        email.HasKey(e => e.OrderId);
        email.Property(e => e.OrderId).ValueGeneratedNever();
        email.HasIndex(e => new { e.SentAt, e.NextAttemptAt });

        var message = model.Entity<NotificationMessage>();
        message.ToTable("NotificationMessages");
        message.Property(m => m.EventKey).HasMaxLength(160);
        message.Property(m => m.Recipient).HasMaxLength(256);
        message.Property(m => m.Subject).HasMaxLength(200);
        message.Property(m => m.Body).HasMaxLength(2000);
        message.HasIndex(m => m.EventKey).IsUnique();
        message.HasIndex(m => new { m.SentAt, m.NextAttemptAt });

        model.Entity<ServiceConsumedEvent>(b =>
        {
            b.ToTable("ConsumedIntegrationEvents");
            b.Property(x => x.ConsumerName).HasMaxLength(80);
            b.Property(x => x.EventKey).HasMaxLength(160);
            b.HasIndex(x => new { x.ConsumerName, x.EventKey }).IsUnique();
        });
    }
}
