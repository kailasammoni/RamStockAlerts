namespace RamStockAlerts.Execution.Interfaces;

public interface IPostSignalMonitor
{
    void OnEntryFilled(string symbol);
}
