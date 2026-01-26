using System.Text.Json;

namespace RamStockAlerts.Execution.Contracts;

public sealed record ExecutionLedgerEvent(string Type, DateTimeOffset TimestampUtc, JsonElement Payload);
