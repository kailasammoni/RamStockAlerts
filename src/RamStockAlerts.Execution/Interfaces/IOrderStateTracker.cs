namespace RamStockAlerts.Execution.Interfaces;

using RamStockAlerts.Execution.Contracts;

public interface IOrderStateTracker
{
    void TrackSubmittedOrder(int orderId, Guid intentId, string symbol, decimal quantity, OrderSide side);
    void ProcessOrderStatus(OrderStatusUpdate update);
    void ProcessFill(FillReport fill);
    void ProcessCommissionReport(string execId, decimal? commission, decimal? realizedPnl);

    BrokerOrderStatus GetOrderStatus(int orderId);
    IReadOnlyList<FillReport> GetFillsForOrder(int orderId);
    IReadOnlyList<FillReport> GetFillsForIntent(Guid intentId);

    decimal GetRealizedPnlToday();
    int GetOpenBracketCount();
}
