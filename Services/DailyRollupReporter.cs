using System.Globalization;
using System.Text;
using System.Text.Json;
using RamStockAlerts.Models;
using Serilog;

namespace RamStockAlerts.Services;

public sealed class DailyRollupReporter
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<int> RunAsync(string journalPath, bool writeToFile, string? outputPath = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(journalPath))
        {
            Log.Error("No journal path provided for daily rollup.");
            return 1;
        }

        if (!File.Exists(journalPath))
        {
            Log.Error("Shadow trade journal not found at {Path}", journalPath);
            return 1;
        }

        var stats = new RollupStats();

        try
        {
            using var stream = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string? line;
            var lineNumber = 0;

            while ((line = await reader.ReadLineAsync()) != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lineNumber++;

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                ShadowTradeJournalEntry? entry;
                try
                {
                    entry = JsonSerializer.Deserialize<ShadowTradeJournalEntry>(line, _jsonOptions);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Could not parse journal line {LineNumber}", lineNumber);
                    continue;
                }

                if (entry == null)
                {
                    continue;
                }

                stats.Record(entry);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to read journal at {Path}", journalPath);
            return 1;
        }

        var report = stats.Render(journalPath);
        Console.WriteLine(report);

        if (writeToFile)
        {
            var finalPath = string.IsNullOrWhiteSpace(outputPath) ? BuildOutputPath(journalPath) : outputPath;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(finalPath) ?? ".");
                await File.WriteAllTextAsync(finalPath, report, cancellationToken);
                Log.Information("Daily rollup written to {Path}", finalPath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to write daily rollup to {Path}", finalPath);
            }
        }

        return 0;
    }

    private static string BuildOutputPath(string journalPath)
    {
        var dir = Path.GetDirectoryName(journalPath);
        var baseName = Path.GetFileNameWithoutExtension(journalPath);
        var outputName = $"{baseName}-rollup-{DateTime.UtcNow:yyyyMMdd}.txt";
        return Path.Combine(string.IsNullOrEmpty(dir) ? "." : dir, outputName);
    }

    private sealed class RollupStats
    {
        private const int MaxAcceptedRowsToShow = 25;

        private readonly Dictionary<string, int> _validatorRejections = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _scarcityRejections = new(StringComparer.OrdinalIgnoreCase);
        private readonly SortedDictionary<int, int> _scoreBuckets = new();
        private readonly List<ShadowTradeJournalEntry> _accepted = new();

        public int TotalCandidates { get; private set; }
        public int ValidatorAccepted { get; private set; }
        public int ValidatorRejected { get; private set; }
        public int ScarcityAccepted { get; private set; }
        public int ScarcityRejected { get; private set; }

        public void Record(ShadowTradeJournalEntry entry)
        {
            TotalCandidates++;
            var decision = entry.DecisionOutcome ?? string.Empty;
            var reason = entry.RejectionReason ?? string.Empty;
            var trace = entry.DecisionTrace ?? new List<string>();

            if (string.Equals(decision, "Accepted", StringComparison.OrdinalIgnoreCase))
            {
                ValidatorAccepted++;
                ScarcityAccepted++;
                _accepted.Add(entry);
            }
            else if (trace.Any(t => t.StartsWith("ScarcityReject", StringComparison.OrdinalIgnoreCase)))
            {
                ValidatorAccepted++;
                ScarcityRejected++;
                AddReason(_scarcityRejections, reason);
            }
            else
            {
                ValidatorRejected++;
                AddReason(_validatorRejections, reason);
            }

            AddScore(entry.DecisionInputs?.Score);
        }

        public string Render(string journalPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Shadow Trade Daily Rollup");
            sb.AppendLine($"Source: {journalPath}");
            sb.AppendLine($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine();

            sb.AppendLine($"Total candidates: {TotalCandidates}");
            sb.AppendLine($"Validator -> accepted: {ValidatorAccepted}, rejected: {ValidatorRejected}");
            AppendReasons(sb, "Top validator rejections", _validatorRejections);
            sb.AppendLine();

            sb.AppendLine($"Scarcity -> accepted: {ScarcityAccepted}, rejected: {ScarcityRejected}");
            AppendReasons(sb, "Scarcity rejection reasons", _scarcityRejections);
            sb.AppendLine();

            AppendScoreDistribution(sb);
            sb.AppendLine();

            AppendAcceptedBlueprints(sb);

            return sb.ToString();
        }

        private static void AppendReasons(StringBuilder sb, string title, Dictionary<string, int> reasons)
        {
            if (reasons.Count == 0)
            {
                sb.AppendLine($"{title}: none");
                return;
            }

            sb.AppendLine(title + ":");
            foreach (var kvp in reasons.OrderByDescending(k => k.Value).ThenBy(k => k.Key).Take(5))
            {
                sb.AppendLine($"- {kvp.Key}: {kvp.Value}");
            }
        }

        private void AppendScoreDistribution(StringBuilder sb)
        {
            sb.AppendLine("Score distribution (bucketed by 10):");
            if (_scoreBuckets.Count == 0)
            {
                sb.AppendLine("- none");
                return;
            }

            foreach (var kvp in _scoreBuckets)
            {
                var lower = kvp.Key;
                var upper = lower + 9;
                sb.AppendLine($"- {lower:D2}-{upper:D2}: {kvp.Value}");
            }
        }

        private void AppendAcceptedBlueprints(StringBuilder sb)
        {
            sb.AppendLine("Accepted blueprints:");
            if (_accepted.Count == 0)
            {
                sb.AppendLine("- none");
                return;
            }

            var ordered = _accepted.OrderBy(x => x.DecisionTimestampUtc ?? DateTimeOffset.MinValue).ToList();
            var limit = Math.Min(MaxAcceptedRowsToShow, ordered.Count);

            for (var i = 0; i < limit; i++)
            {
                var entry = ordered[i];
                var ts = entry.DecisionTimestampUtc.HasValue
                    ? entry.DecisionTimestampUtc.Value.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)
                    : "unknown";
                var score = (entry.DecisionInputs?.Score).GetValueOrDefault().ToString("0.##", CultureInfo.InvariantCulture);
                var spread = (entry.ObservedMetrics?.Spread).GetValueOrDefault().ToString("0.####", CultureInfo.InvariantCulture);
                var qi = (entry.ObservedMetrics?.QueueImbalance).GetValueOrDefault().ToString("0.##", CultureInfo.InvariantCulture);
                var tapeAccel = (entry.ObservedMetrics?.TapeAcceleration).GetValueOrDefault().ToString("0.##", CultureInfo.InvariantCulture);

                sb.AppendLine($"- {ts} {entry.Symbol} {entry.Direction} score={score} spread={spread} qi={qi} tapeAccel={tapeAccel}");
            }

            if (ordered.Count > MaxAcceptedRowsToShow)
            {
                sb.AppendLine($"- ... {ordered.Count - MaxAcceptedRowsToShow} more accepted");
            }
        }

        private static void AddReason(Dictionary<string, int> reasons, string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                return;
            }

            reasons.TryGetValue(reason, out var count);
            reasons[reason] = count + 1;
        }

        private void AddScore(decimal? score)
        {
            if (!score.HasValue)
            {
                return;
            }

            var bucket = (int)Math.Floor(score.Value / 10m);
            var lower = bucket * 10;
            _scoreBuckets.TryGetValue(lower, out var count);
            _scoreBuckets[lower] = count + 1;
        }
    }
}
