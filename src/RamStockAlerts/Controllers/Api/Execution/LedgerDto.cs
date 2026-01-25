namespace RamStockAlerts.Controllers.Api.Execution;

using RamStockAlerts.Execution.Contracts;

/// <summary>
/// DTO for ledger query response.
/// </summary>
public class LedgerDto
{
    /// <summary>
    /// Recent order intents.
    /// </summary>
    public List<OrderIntent> Intents { get; set; } = new();

    /// <summary>
    /// Recent bracket intents.
    /// </summary>
    public List<BracketIntent> Brackets { get; set; } = new();

    /// <summary>
    /// Recent execution results.
    /// </summary>
    public List<ExecutionResult> Results { get; set; } = new();
}
