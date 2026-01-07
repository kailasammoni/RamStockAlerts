using RamStockAlerts.Models;

namespace RamStockAlerts.Models;

public class SignalLifecycle
{
    public int Id { get; set; }
    public int SignalId { get; set; }
    public SignalStatus Status { get; set; }
    public string? Reason { get; set; }
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
    public decimal? SpreadAtEvent { get; set; }
    public decimal? PrintsPerSecond { get; set; }
}
