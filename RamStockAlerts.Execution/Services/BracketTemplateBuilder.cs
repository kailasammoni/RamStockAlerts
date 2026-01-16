namespace RamStockAlerts.Execution.Services;

using RamStockAlerts.Execution.Contracts;

/// <summary>
/// Builds BracketIntent from ExecutionRequest using template-based sizing and level calculation.
/// Supports VOL_A (fast scalp) and VOL_B (wider trend) templates.
/// </summary>
public sealed class BracketTemplateBuilder
{
    /// <summary>
    /// Builds a BracketIntent from an ExecutionRequest using the specified template.
    /// </summary>
    /// <param name="req">Execution request with symbol, side, pricing, and template.</param>
    /// <returns>BracketIntent with entry, stop-loss, and take-profit orders.</returns>
    public BracketIntent Build(ExecutionRequest req)
    {
        // Calculate stop distance based on template
        var stopDistance = CalculateStopDistance(req);

        // Calculate sizing
        var riskBudgetUsd = req.AccountEquityUsd * req.RiskPerTradePct;
        var rawQty = Math.Floor(riskBudgetUsd / stopDistance);

        // Apply max notional cap
        var maxNotional = req.AccountEquityUsd * req.MaxNotionalPct;
        var maxQtyByNotional = Math.Floor(maxNotional / req.ReferencePrice);
        var qty = Math.Min(rawQty, maxQtyByNotional);

        // Reject if quantity is zero or negative
        if (qty <= 0)
        {
            return CreateRejectedBracket(req, "SizingTooSmall");
        }

        // Calculate profit target distance
        var profitDistance = CalculateProfitDistance(req, stopDistance);

        // Build price levels
        var (entryPrice, stopPrice, takeProfitPrice) = CalculatePriceLevels(
            req.Side,
            req.ReferencePrice,
            stopDistance,
            profitDistance
        );

        // Build bracket intent
        var intentId = Guid.NewGuid();
        var timestamp = DateTime.UtcNow;

        var entry = new OrderIntent
        {
            IntentId = intentId,
            Mode = TradingMode.Paper, // Default to Paper for now
            Symbol = req.Symbol,
            Side = req.Side,
            Type = OrderType.Market, // Templates use Market entry for now
            Quantity = qty,
            Tif = TimeInForce.Day,
            CreatedUtc = timestamp,
            Tags = new Dictionary<string, string>
            {
                ["Template"] = req.Template,
                ["StopDistance"] = stopDistance.ToString("F4"),
                ["ProfitDistance"] = profitDistance.ToString("F4")
            }
        };

        var stopLoss = new OrderIntent
        {
            IntentId = Guid.NewGuid(),
            Mode = TradingMode.Paper,
            Symbol = req.Symbol,
            Side = req.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy,
            Type = OrderType.Stop,
            Quantity = qty,
            StopPrice = stopPrice,
            Tif = TimeInForce.Day,
            CreatedUtc = timestamp,
            Tags = new Dictionary<string, string>
            {
                ["Template"] = req.Template
            }
        };

        var takeProfit = new OrderIntent
        {
            IntentId = Guid.NewGuid(),
            Mode = TradingMode.Paper,
            Symbol = req.Symbol,
            Side = req.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy,
            Type = OrderType.Limit,
            Quantity = qty,
            LimitPrice = takeProfitPrice,
            Tif = TimeInForce.Day,
            CreatedUtc = timestamp,
            Tags = new Dictionary<string, string>
            {
                ["Template"] = req.Template
            }
        };

        return new BracketIntent
        {
            Entry = entry,
            StopLoss = stopLoss,
            TakeProfit = takeProfit,
            OcoGroupId = intentId.ToString()
        };
    }

    /// <summary>
    /// Calculates stop distance based on template and volatility proxy.
    /// </summary>
    private decimal CalculateStopDistance(ExecutionRequest req)
    {
        var spread = req.VolatilityProxy ?? 0m;

        return req.Template.ToUpperInvariant() switch
        {
            "VOL_A" => Math.Max(req.ReferencePrice * 0.003m, spread * 6m),
            "VOL_B" => Math.Max(req.ReferencePrice * 0.006m, spread * 10m),
            _ => throw new ArgumentException($"Unknown template: {req.Template}", nameof(req))
        };
    }

    /// <summary>
    /// Calculates profit target distance based on template and stop distance.
    /// </summary>
    private decimal CalculateProfitDistance(ExecutionRequest req, decimal stopDistance)
    {
        return req.Template.ToUpperInvariant() switch
        {
            "VOL_A" => stopDistance * 1.2m,
            "VOL_B" => stopDistance * 1.8m,
            _ => throw new ArgumentException($"Unknown template: {req.Template}", nameof(req))
        };
    }

    /// <summary>
    /// Calculates entry, stop, and take-profit price levels based on side and distances.
    /// </summary>
    private (decimal EntryPrice, decimal StopPrice, decimal TakeProfitPrice) CalculatePriceLevels(
        OrderSide side,
        decimal referencePrice,
        decimal stopDistance,
        decimal profitDistance)
    {
        if (side == OrderSide.Buy)
        {
            // Buy: Stop below, Profit above
            var entryPrice = referencePrice;
            var stopPrice = referencePrice - stopDistance;
            var takeProfitPrice = referencePrice + profitDistance;
            return (entryPrice, stopPrice, takeProfitPrice);
        }
        else
        {
            // Sell: Stop above, Profit below
            var entryPrice = referencePrice;
            var stopPrice = referencePrice + stopDistance;
            var takeProfitPrice = referencePrice - profitDistance;
            return (entryPrice, stopPrice, takeProfitPrice);
        }
    }

    /// <summary>
    /// Creates a rejected bracket with empty orders for sizing failures.
    /// </summary>
    private BracketIntent CreateRejectedBracket(ExecutionRequest req, string rejectionReason)
    {
        var intentId = Guid.NewGuid();
        var timestamp = DateTime.UtcNow;

        var entry = new OrderIntent
        {
            IntentId = intentId,
            Mode = TradingMode.Paper,
            Symbol = req.Symbol,
            Side = req.Side,
            Type = OrderType.Market,
            Quantity = 0,
            Tif = TimeInForce.Day,
            CreatedUtc = timestamp,
            Tags = new Dictionary<string, string>
            {
                ["Template"] = req.Template,
                ["RejectionReason"] = rejectionReason
            }
        };

        return new BracketIntent
        {
            Entry = entry,
            StopLoss = null,
            TakeProfit = null,
            OcoGroupId = intentId.ToString()
        };
    }
}
