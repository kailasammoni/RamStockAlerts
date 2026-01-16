namespace RamStockAlerts.Execution.Contracts;

/// <summary>
/// Configuration options for execution risk management and safety rails.
/// Loaded from appsettings.json under "Execution" section.
/// </summary>
public class ExecutionOptions
{
    /// <summary>
    /// Whether execution is enabled (default: false).
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Trading mode (Paper | Live; default: Paper).
    /// </summary>
    public TradingMode Mode { get; set; } = TradingMode.Paper;

    /// <summary>
    /// Emergency kill switch to reject all orders (default: false).
    /// </summary>
    public bool KillSwitch { get; set; } = false;

    /// <summary>
    /// Maximum orders allowed per calendar day (default: 20).
    /// </summary>
    public int MaxOrdersPerDay { get; set; } = 20;

    /// <summary>
    /// Maximum bracket orders allowed per calendar day (default: 10).
    /// </summary>
    public int MaxBracketsPerDay { get; set; } = 10;

    /// <summary>
    /// Maximum concurrent open positions (mocked as active brackets; default: 3).
    /// </summary>
    public int MaxOpenPositions { get; set; } = 3;

    /// <summary>
    /// Maximum notional per trade as % of account equity (default: 10).
    /// </summary>
    public decimal MaxNotionalPerTradePct { get; set; } = 10m;

    /// <summary>
    /// Maximum loss per calendar day in USD (default: 200).
    /// </summary>
    public decimal MaxLossPerDayUsd { get; set; } = 200m;

    /// <summary>
    /// Cooldown seconds between orders on same symbol (default: 120).
    /// </summary>
    public int SymbolCooldownSeconds { get; set; } = 120;

    /// <summary>
    /// Minimum seconds between any orders globally (default: 10).
    /// </summary>
    public int MinSecondsBetweenOrders { get; set; } = 10;

    /// <summary>
    /// Name of the broker client to use (default: "Fake").
    /// </summary>
    public string? Broker { get; set; } = "Fake";
}
