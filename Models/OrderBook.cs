namespace RamStockAlerts.Models;

public class OrderBook
{
    public decimal BidAskRatio { get; set; }
    public decimal TotalBidSize { get; set; }
    public decimal TotalAskSize { get; set; }
    public decimal Spread { get; set; }
    public DateTime Timestamp { get; set; }
}
