namespace RamStockAlerts.Models;

public class VwapData
{
    public decimal CurrentPrice { get; set; }
    public decimal VwapPrice { get; set; }
    public bool HasReclaim { get; set; }
}
