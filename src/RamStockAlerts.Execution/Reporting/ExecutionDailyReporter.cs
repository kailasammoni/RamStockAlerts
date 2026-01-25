namespace RamStockAlerts.Execution.Reporting;

using System.Text;
using RamStockAlerts.Execution.Contracts;
using RamStockAlerts.Execution.Storage;

public sealed class ExecutionDailyReporter
{
    public Task RunAsync(
        string ledgerPath,
        bool writeToFile,
        string? outputPath,
        DateOnly? reportDate = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ledgerPath))
            throw new ArgumentException("Ledger path is required", nameof(ledgerPath));

        var date = reportDate ?? DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);

        var ledger = new JsonlExecutionLedger(ledgerPath);
        var report = GenerateMarkdown(date, ledger.GetIntents(), ledger.GetBrackets(), ledger.GetResults());

        if (writeToFile)
        {
            var path = ResolveOutputPath(outputPath, date);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, report, Encoding.UTF8);
        }
        else
        {
            Console.WriteLine(report);
        }

        return Task.CompletedTask;
    }

    public string GenerateMarkdown(
        DateOnly date,
        IReadOnlyList<OrderIntent> intents,
        IReadOnlyList<BracketIntent> brackets,
        IReadOnlyList<ExecutionResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Execution End-of-Day Report ({date:yyyy-MM-dd})");
        sb.AppendLine();

        var intentsForDay = intents.Where(i => DateOnly.FromDateTime(i.CreatedUtc.UtcDateTime) == date).ToList();
        var bracketsForDay = brackets.Where(b => DateOnly.FromDateTime(b.Entry.CreatedUtc.UtcDateTime) == date).ToList();
        var resultsForDay = results.Where(r => DateOnly.FromDateTime(r.TimestampUtc.UtcDateTime) == date).ToList();

        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine($"- Intents: {intentsForDay.Count}");
        sb.AppendLine($"- Brackets: {bracketsForDay.Count}");
        sb.AppendLine($"- Results: {resultsForDay.Count}");
        sb.AppendLine();

        if (resultsForDay.Count > 0)
        {
            sb.AppendLine("### Status Counts");
            sb.AppendLine();
            foreach (var grp in resultsForDay.GroupBy(r => r.Status).OrderByDescending(g => g.Count()))
            {
                sb.AppendLine($"- {grp.Key}: {grp.Count()}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Symbol Breakdown");
        sb.AppendLine();

        var symbols = intentsForDay
            .Select(i => i.Symbol)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (symbols.Count == 0)
        {
            sb.AppendLine("_No activity recorded._");
            sb.AppendLine();
            return sb.ToString();
        }

        foreach (var symbol in symbols)
        {
            var symIntents = intentsForDay.Where(i => string.Equals(i.Symbol, symbol, StringComparison.OrdinalIgnoreCase)).ToList();
            var symIntentIds = symIntents.Select(i => i.IntentId).ToHashSet();
            var symResults = resultsForDay.Where(r => symIntentIds.Contains(r.IntentId)).ToList();

            var buyQty = symIntents.Where(i => i.Side == OrderSide.Buy).Sum(i => i.Quantity ?? 0m);
            var sellQty = symIntents.Where(i => i.Side == OrderSide.Sell).Sum(i => i.Quantity ?? 0m);

            sb.AppendLine($"### {symbol}");
            sb.AppendLine();
            sb.AppendLine($"- Intents: {symIntents.Count}");
            sb.AppendLine($"- BuyQty: {buyQty:F0}");
            sb.AppendLine($"- SellQty: {sellQty:F0}");
            if (symResults.Count > 0)
            {
                sb.AppendLine($"- Results: {symResults.Count}");
                var statuses = symResults.GroupBy(r => r.Status).ToDictionary(g => g.Key, g => g.Count());
                sb.AppendLine($"- Statuses: {string.Join(", ", statuses.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}"))}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Recent Results");
        sb.AppendLine();

        foreach (var r in resultsForDay.OrderByDescending(r => r.TimestampUtc).Take(50))
        {
            var brokerIds = r.BrokerOrderIds.Count > 0 ? string.Join(",", r.BrokerOrderIds) : "N/A";
            var fill = r.FilledQuantity.HasValue || r.AvgFillPrice.HasValue
                ? $" fillQty={r.FilledQuantity?.ToString("F0") ?? "?"} avgFill={r.AvgFillPrice?.ToString("F4") ?? "?"}"
                : string.Empty;

            sb.AppendLine($"- {r.TimestampUtc:HH:mm:ss} intent={r.IntentId} status={r.Status} broker={r.BrokerName ?? "?"} orderIds={brokerIds}{fill}");
            if (!string.IsNullOrWhiteSpace(r.RejectionReason))
            {
                sb.AppendLine($"  - reason: {r.RejectionReason}");
            }
        }

        sb.AppendLine();
        return sb.ToString();
    }

    private static string ResolveOutputPath(string? outputPath, DateOnly date)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return Path.Combine("logs", $"execution-eod-{date:yyyy-MM-dd}.md");
        }

        // If outputPath is a directory, write default filename inside it.
        if (Directory.Exists(outputPath))
        {
            return Path.Combine(outputPath, $"execution-eod-{date:yyyy-MM-dd}.md");
        }

        return outputPath;
    }
}
