using System;
using System.Collections.Generic;
using RamStockAlerts.Models;
using RamStockAlerts.Services.Signals;

namespace RamStockAlerts.Tests.TestDoubles;

internal sealed class TestTradeJournal : ITradeJournal
{
    public Guid SessionId { get; } = Guid.NewGuid();

    public List<TradeJournalEntry> Entries { get; } = new();

    public bool TryEnqueue(TradeJournalEntry entry)
    {
        Entries.Add(entry);
        return true;
    }
}

