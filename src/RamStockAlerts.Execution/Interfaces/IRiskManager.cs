namespace RamStockAlerts.Execution.Interfaces;

using RamStockAlerts.Execution.Contracts;

/// <summary>
/// Interface for risk management / validation.
/// Evaluates orders and brackets against safety rails: kill switch, daily limits, cooldowns, etc.
/// </summary>
public interface IRiskManager
{
    /// <summary>
    /// Validate a single order intent against risk rules.
    /// </summary>
    /// <param name="intent">The order intent to validate.</param>
    /// <param name="ledger">Optional execution ledger for tracking state (orders today, cooldowns, etc.)</param>
    /// <param name="now">Current UTC time for time-based checks (cooldowns).</param>
    /// <returns>A RiskDecision indicating if the order is allowed.</returns>
    RiskDecision Validate(OrderIntent intent, IExecutionLedger? ledger = null, DateTimeOffset? now = null);

    /// <summary>
    /// Validate a bracket intent against risk rules.
    /// </summary>
    /// <param name="intent">The bracket intent to validate.</param>
    /// <param name="ledger">Optional execution ledger for tracking state (orders today, cooldowns, etc.)</param>
    /// <param name="now">Current UTC time for time-based checks (cooldowns).</param>
    /// <returns>A RiskDecision indicating if the bracket is allowed.</returns>
    RiskDecision Validate(BracketIntent intent, IExecutionLedger? ledger = null, DateTimeOffset? now = null);
}
