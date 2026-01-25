using RamStockAlerts.Models;

namespace RamStockAlerts.Services.Signals;

public interface ITradeJournal
{
    Guid SessionId { get; }
    bool TryEnqueue(TradeJournalEntry entry);
}
