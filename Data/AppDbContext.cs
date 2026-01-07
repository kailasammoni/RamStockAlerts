using Microsoft.EntityFrameworkCore;
using RamStockAlerts.Models;

namespace RamStockAlerts.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<TradeSignal> TradeSignals => Set<TradeSignal>();
    public DbSet<SignalLifecycle> SignalLifecycles => Set<SignalLifecycle>();
    public DbSet<EventStoreEntry> EventStoreEntries => Set<EventStoreEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TradeSignal>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Ticker).IsRequired().HasMaxLength(10);
            entity.Property(e => e.Entry).HasPrecision(18, 4);
            entity.Property(e => e.Stop).HasPrecision(18, 4);
            entity.Property(e => e.Target).HasPrecision(18, 4);
            entity.Property(e => e.Score).HasPrecision(5, 2);
            entity.Property(e => e.ExecutionPrice).HasPrecision(18, 4);
            entity.Property(e => e.ExitPrice).HasPrecision(18, 4);
            entity.Property(e => e.PnL).HasPrecision(18, 4);
            entity.HasIndex(e => e.Ticker);
            entity.HasIndex(e => e.Timestamp);
        });

        modelBuilder.Entity<SignalLifecycle>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).HasConversion<int>();
            entity.Property(e => e.Reason).HasMaxLength(512);
            entity.HasIndex(e => e.SignalId);
            entity.HasIndex(e => e.OccurredAt);
        });

        modelBuilder.Entity<EventStoreEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.AggregateId).IsRequired().HasMaxLength(100);
            entity.Property(e => e.EventType).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Payload).IsRequired();
            entity.Property(e => e.CorrelationId).HasMaxLength(100);
            entity.HasIndex(e => e.AggregateId);
            entity.HasIndex(e => e.EventType);
            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => e.CorrelationId);
        });
    }
}
