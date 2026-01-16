namespace RamStockAlerts.Controllers.Api.Execution;

using System.ComponentModel.DataAnnotations;
using RamStockAlerts.Execution.Contracts;

/// <summary>
/// DTO for creating a bracket intent via API.
/// </summary>
public class BracketIntentDto
{
    /// <summary>
    /// Entry order.
    /// </summary>
    [Required]
    public OrderIntentDto Entry { get; set; } = null!;

    /// <summary>
    /// Optional take-profit order.
    /// </summary>
    public OrderIntentDto? TakeProfit { get; set; }

    /// <summary>
    /// Optional stop-loss order.
    /// </summary>
    public OrderIntentDto? StopLoss { get; set; }

    /// <summary>
    /// Optional OCO group ID.
    /// </summary>
    public string? OcoGroupId { get; set; }

    /// <summary>
    /// Map DTO to domain BracketIntent.
    /// </summary>
    public BracketIntent ToBracketIntent()
    {
        return new BracketIntent
        {
            Entry = Entry.ToOrderIntent(),
            TakeProfit = TakeProfit?.ToOrderIntent(),
            StopLoss = StopLoss?.ToOrderIntent(),
            OcoGroupId = OcoGroupId
        };
    }
}
