using System;
using System.Collections.Generic;
using RamStockAlerts.Models;
using RamStockAlerts.Services;

namespace RamStockAlerts.Tests.TestDoubles;

internal sealed class TestShadowTradeJournal : IShadowTradeJournal
{
    public Guid SessionId { get; } = Guid.NewGuid();

    public List<ShadowTradeJournalEntry> Entries { get; } = new();

    public bool TryEnqueue(ShadowTradeJournalEntry entry)
    {
        Entries.Add(entry);
        return true;
    }
}
