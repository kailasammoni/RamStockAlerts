using System.Globalization;
using System.Text;
using System.Text.Json;
using RamStockAlerts.Models;
using Serilog;

namespace RamStockAlerts.Services;

public sealed class DailyRollupReporter
{
    private readonly ITradeOutcomeLabeler? _outcomeLabeler;
    private readonly IOutcomeSummaryStore? _outcomeStore;
    private readonly IJournalRotationService? _journalRotationService;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // Constructor for backward compatibility (no outcome labeling or rotation)
    public DailyRollupReporter()
    {
        _outcomeLabeler = null;
        _outcomeStore = null;
        _journalRotationService = null;
    }

    // Constructor with DI for outcome labeling (Phase 2.1-2.2)
    public DailyRollupReporter(ITradeOutcomeLabeler? outcomeLabeler, IOutcomeSummaryStore? outcomeStore)
    {
        _outcomeLabeler = outcomeLabeler;
        _outcomeStore = outcomeStore;
        _journalRotationService = null;
    }

    // Constructor with full DI for outcomes + rotation (Phase 2.4)
    public DailyRollupReporter(
        ITradeOutcomeLabeler? outcomeLabeler,
        IOutcomeSummaryStore? outcomeStore,
        IJournalRotationService? journalRotationService)
    {
        _outcomeLabeler = outcomeLabeler;
        _outcomeStore = outcomeStore;
        _journalRotationService = journalRotationService;
    }

    public async Task<int> RunAsync(string journalPath, bool writeToFile, string? outputPath = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(journalPath))
        {
            Log.Error("No journal path provided for daily rollup.");
            return 1;
        }

        if (!File.Exists(journalPath))
        {
            Log.Error("Trade journal not found at {Path}", journalPath);
            return 1;
        }

        // Phase 2.4: Rotate journal before processing if rotation service is available
        if (_journalRotationService != null)
        {
            try
            {
                var rotated = await _journalRotationService.RotateJournalAsync(journalPath, cancellationToken);
                if (rotated)
                {
                    Log.Information("Journal rotated before processing");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to rotate journal (will continue processing current journal)");
            }
        }

        // Recreate the current journal if it was rotated but is empty now
        // Otherwise proceed with current journal if it exists
        if (!File.Exists(journalPath))
        {
            Log.Information("Journal was rotated and is now empty, creating new journal");
            File.WriteAllText(journalPath, string.Empty);
        }

        var stats = new RollupStats();
        var acceptedEntries = new List<TradeJournalEntry>();

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

                TradeJournalEntry? entry;
                try
                {
                    entry = JsonSerializer.Deserialize<TradeJournalEntry>(line, _jsonOptions);
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

                // Track accepted entries for outcome labeling
                if (string.Equals(entry.DecisionOutcome, "Accepted", StringComparison.OrdinalIgnoreCase))
                {
                    acceptedEntries.Add(entry);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to read journal at {Path}", journalPath);
            return 1;
        }

        // Phase 2.4: Load previous outcomes from store and include in stats
        if (_outcomeStore != null)
        {
            try
            {
                var previousOutcomes = await _outcomeStore.GetAllOutcomesAsync(cancellationToken);
                foreach (var outcome in previousOutcomes)
                {
                    stats.RecordOutcome(outcome);
                }
                
                if (previousOutcomes.Count > 0)
                {
                    Log.Information("Loaded {Count} previous outcomes into rollup", previousOutcomes.Count);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to load previous outcomes (continuing without historical context)");
            }
        }

        // Label outcomes if labeler and store are available
        if (_outcomeLabeler != null && _outcomeStore != null && acceptedEntries.Count > 0)
        {
            try
            {
                var outcomes = await _outcomeLabeler.LabelOutcomesAsync(acceptedEntries, cancellationToken: cancellationToken);
                await _outcomeStore.AppendOutcomesAsync(outcomes, cancellationToken);
                
                // Record outcomes in stats for rollup report
                foreach (var outcome in outcomes)
                {
                    stats.RecordOutcome(outcome);
                }
                
                Log.Information("Labeled and stored {Count} outcomes", outcomes.Count);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to label outcomes");
            }
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
        private readonly List<TradeJournalEntry> _accepted = new();

        // Outcome tracking for Phase 2
        private int _outcomeWins = 0;
        private int _outcomeLosses = 0;
        private int _outcomeOpens = 0;
        private int _outcomeNoHits = 0;
        private decimal _totalOutcomePnl = 0m;
        private decimal _sumWinRMultiples = 0m;
        private decimal _sumLossRMultiples = 0m;
        private int _winCount = 0;
        private int _lossCount = 0;

        public int TotalCandidates { get; private set; }
        public int ValidatorAccepted { get; private set; }
        public int ValidatorRejected { get; private set; }
        public int ScarcityAccepted { get; private set; }
        public int ScarcityRejected { get; private set; }

        public void Record(TradeJournalEntry entry)
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

        /// <summary>
        /// Record outcome metrics from labeled trades.
        /// </summary>
        public void RecordOutcome(TradeOutcome outcome)
        {
            if (outcome == null)
                return;

            // Track outcome type
            if (outcome.OutcomeType == "HitTarget")
            {
                _outcomeWins++;
                _winCount++;
                if (outcome.RiskMultiple.HasValue)
                    _sumWinRMultiples += outcome.RiskMultiple.Value;
            }
            else if (outcome.OutcomeType == "HitStop")
            {
                _outcomeLosses++;
                _lossCount++;
                if (outcome.RiskMultiple.HasValue)
                    _sumLossRMultiples += Math.Abs(outcome.RiskMultiple.Value);
            }
            else if (outcome.OutcomeType == "NoExit")
            {
                _outcomeOpens++;
            }
            else if (outcome.OutcomeType == "NoHit")
            {
                _outcomeNoHits++;
            }

            // Track P&L
            if (outcome.PnlUsd.HasValue)
                _totalOutcomePnl += outcome.PnlUsd.Value;
        }

        /// <summary>
        /// Get calculated performance metrics from outcomes.
        /// </summary>
        public PerformanceMetrics GetPerformanceMetrics()
        {
            var totalClosed = _outcomeWins + _outcomeLosses;
            var winRate = totalClosed > 0 ? (decimal)_outcomeWins / totalClosed : (decimal?)null;
            var avgWinR = _winCount > 0 ? _sumWinRMultiples / _winCount : (decimal?)null;
            var avgLossR = _lossCount > 0 ? _sumLossRMultiples / _lossCount : (decimal?)null;

            return new PerformanceMetrics
            {
                WinCount = _outcomeWins,
                LossCount = _outcomeLosses,
                OpenCount = _outcomeOpens,
                NoHitCount = _outcomeNoHits,
                TotalSignals = _outcomeWins + _outcomeLosses + _outcomeOpens + _outcomeNoHits,
                TotalPnlUsd = _totalOutcomePnl,
                AvgWinRMultiple = avgWinR,
                AvgLossRMultiple = avgLossR
            };
        }

        public string Render(string journalPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Trade Daily Rollup");
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

            // Add performance metrics section (Phase 2.2)
            AppendPerformanceMetrics(sb);
            sb.AppendLine();

            AppendAcceptedBlueprints(sb);

            return sb.ToString();
        }

        private void AppendPerformanceMetrics(StringBuilder sb)
        {
            var metrics = GetPerformanceMetrics();

            if (metrics.TotalSignals == 0)
            {
                sb.AppendLine("Performance Metrics: no outcomes yet");
                return;
            }

            sb.AppendLine("Performance Metrics:");
            sb.AppendLine($"- Total signals: {metrics.TotalSignals}");
            sb.AppendLine($"- Hits target: {metrics.WinCount}");
            sb.AppendLine($"- Hits stop: {metrics.LossCount}");
            sb.AppendLine($"- Still open: {metrics.OpenCount}");
            sb.AppendLine($"- No hit: {metrics.NoHitCount}");

            if (metrics.WinRate.HasValue)
                sb.AppendLine($"- Win rate: {(metrics.WinRate.Value * 100).ToString("F1", CultureInfo.InvariantCulture)}%");

            if (metrics.AvgWinRMultiple.HasValue)
                sb.AppendLine($"- Avg +R (wins): {metrics.AvgWinRMultiple.Value.ToString("F2", CultureInfo.InvariantCulture)}R");

            if (metrics.AvgLossRMultiple.HasValue)
                sb.AppendLine($"- Avg -R (losses): {metrics.AvgLossRMultiple.Value.ToString("F2", CultureInfo.InvariantCulture)}R");

            if (metrics.Expectancy.HasValue)
                sb.AppendLine($"- Expectancy: {metrics.Expectancy.Value.ToString("F2", CultureInfo.InvariantCulture)}R per trade");

            sb.AppendLine($"- Total P&L: ${metrics.TotalPnlUsd.ToString("F2", CultureInfo.InvariantCulture)}");
            
            // Append warning thresholds
            AppendMetricsWarnings(sb, metrics);
        }

        private void AppendMetricsWarnings(StringBuilder sb, PerformanceMetrics metrics)
        {
            var warnings = new List<string>();

            // Check win rate threshold (target: >= 60%)
            if (metrics.WinRate.HasValue)
            {
                if (metrics.WinRate.Value < 0.60m)
                {
                    warnings.Add($"⚠ Win rate {(metrics.WinRate.Value * 100).ToString("F1", CultureInfo.InvariantCulture)}% below target (≥60%)");
                }
            }

            // Check expectancy threshold (target: > 0, target: >= 0.25R)
            if (metrics.Expectancy.HasValue)
            {
                if (metrics.Expectancy.Value < 0.25m)
                {
                    warnings.Add($"⚠ Expectancy {metrics.Expectancy.Value.ToString("F2", CultureInfo.InvariantCulture)}R below target (≥0.25R)");
                }
            }

            // Check for insufficient closed trades
            var totalClosed = metrics.WinCount + metrics.LossCount;
            if (totalClosed < 3)
            {
                warnings.Add($"⚠ Only {totalClosed} closed trade(s) - insufficient sample for metrics");
            }

            // Check if average loss exceeds average win (unfavorable risk/reward)
            if (metrics.AvgWinRMultiple.HasValue && metrics.AvgLossRMultiple.HasValue)
            {
                if (metrics.AvgWinRMultiple.Value <= metrics.AvgLossRMultiple.Value)
                {
                    warnings.Add($"⚠ Avg loss {metrics.AvgLossRMultiple.Value:F2}R >= avg win {metrics.AvgWinRMultiple.Value:F2}R (unfavorable risk/reward)");
                }
            }

            if (warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("⚠ WARNINGS:");
                foreach (var warning in warnings)
                {
                    sb.AppendLine(warning);
                }
            }
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

