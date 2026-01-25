namespace RamStockAlerts.Controllers.Api.Execution;

using System.ComponentModel.DataAnnotations;
using RamStockAlerts.Execution.Contracts;

/// <summary>
/// DTO for creating an order intent via API.
/// </summary>
public class OrderIntentDto
{
    /// <summary>
    /// Optional decision ID that led to this intent.
    /// </summary>
    public Guid? DecisionId { get; set; }

    /// <summary>
    /// Symbol to trade (e.g., "AAPL").
    /// </summary>
    [Required]
    public string Symbol { get; set; } = null!;

    /// <summary>
    /// Order side (Buy or Sell).
    /// </summary>
    [Required]
    public string Side { get; set; } = null!;

    /// <summary>
    /// Order type (Market, Limit, Stop, StopLimit).
    /// </summary>
    [Required]
    public string Type { get; set; } = null!;

    /// <summary>
    /// Quantity in shares (required for F2).
    /// </summary>
    [Required]
    [Range(0.01, double.MaxValue, ErrorMessage = "Quantity must be greater than 0")]
    public decimal Quantity { get; set; }

    /// <summary>
    /// Limit price (for Limit and StopLimit orders).
    /// </summary>
    public decimal? LimitPrice { get; set; }

    /// <summary>
    /// Stop price (for Stop and StopLimit orders).
    /// </summary>
    public decimal? StopPrice { get; set; }

    /// <summary>
    /// Time in force (Day or GTC).
    /// </summary>
    public string? Tif { get; set; }

    /// <summary>
    /// Optional metadata tags.
    /// </summary>
    public Dictionary<string, string>? Tags { get; set; }

    /// <summary>
    /// Map DTO to domain OrderIntent.
    /// </summary>
    public OrderIntent ToOrderIntent()
    {
        return new OrderIntent
        {
            DecisionId = DecisionId,
            Symbol = Symbol,
            Side = Enum.Parse<OrderSide>(Side, ignoreCase: true),
            Type = Enum.Parse<OrderType>(Type, ignoreCase: true),
            Quantity = Quantity,
            NotionalUsd = null, // F2: quantity only, notional support deferred to F4
            LimitPrice = LimitPrice,
            StopPrice = StopPrice,
            Tif = string.IsNullOrWhiteSpace(Tif) 
                ? TimeInForce.Day 
                : Enum.Parse<TimeInForce>(Tif, ignoreCase: true),
            Tags = Tags
        };
    }
}
