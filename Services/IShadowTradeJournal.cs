using RamStockAlerts.Models;

namespace RamStockAlerts.Services;

public interface IShadowTradeJournal
{
    Guid SessionId { get; }
    bool TryEnqueue(ShadowTradeJournalEntry entry);
}
