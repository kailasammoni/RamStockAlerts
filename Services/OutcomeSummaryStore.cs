using System.Text.Json;
using RamStockAlerts.Models;
using Serilog;

namespace RamStockAlerts.Services;

/// <summary>
/// Interface for storing and retrieving trade outcomes.
/// </summary>
public interface IOutcomeSummaryStore
{
    /// <summary>
    /// Append a single outcome to persistent storage.
    /// </summary>
    Task AppendOutcomeAsync(TradeOutcome outcome, CancellationToken cancellationToken = default);

    /// <summary>
    /// Append multiple outcomes to persistent storage.
    /// </summary>
    Task AppendOutcomesAsync(List<TradeOutcome> outcomes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Read all outcomes for a given date (UTC).
    /// </summary>
    Task<List<TradeOutcome>> GetOutcomesByDateAsync(DateOnly dateUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Read all outcomes in the store.
    /// </summary>
    Task<List<TradeOutcome>> GetAllOutcomesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get outcomes for a date range.
    /// </summary>
    Task<List<TradeOutcome>> GetOutcomesByDateRangeAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default);
}

/// <summary>
/// File-based outcome store using JSONL format (deterministic audit trail).
/// One outcome per line; outcomes are immutable once written.
/// </summary>
public sealed class FileBasedOutcomeSummaryStore : IOutcomeSummaryStore
{
    private readonly string _filePath;
    private readonly global::Serilog.ILogger _logger = Log.ForContext<FileBasedOutcomeSummaryStore>();
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public FileBasedOutcomeSummaryStore(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
    }

    public async Task AppendOutcomeAsync(TradeOutcome outcome, CancellationToken cancellationToken = default)
    {
        if (outcome == null)
        {
            throw new ArgumentNullException(nameof(outcome));
        }

        try
        {
            var json = JsonSerializer.Serialize(outcome, _jsonOptions);
            await File.AppendAllTextAsync(_filePath, json + Environment.NewLine, cancellationToken);
            _logger.Debug("Appended outcome for {DecisionId} to {FilePath}", outcome.DecisionId, _filePath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to append outcome to {FilePath}", _filePath);
            throw;
        }
    }

    public async Task AppendOutcomesAsync(List<TradeOutcome> outcomes, CancellationToken cancellationToken = default)
    {
        if (outcomes == null)
        {
            throw new ArgumentNullException(nameof(outcomes));
        }

        if (outcomes.Count == 0)
        {
            return;
        }

        try
        {
            var lines = outcomes
                .Select(o => JsonSerializer.Serialize(o, _jsonOptions))
                .ToList();

            var content = string.Join(Environment.NewLine, lines) + Environment.NewLine;
            await File.AppendAllTextAsync(_filePath, content, cancellationToken);
            _logger.Information("Appended {Count} outcomes to {FilePath}", outcomes.Count, _filePath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to append {Count} outcomes to {FilePath}", outcomes.Count, _filePath);
            throw;
        }
    }

    public async Task<List<TradeOutcome>> GetOutcomesByDateAsync(DateOnly dateUtc, CancellationToken cancellationToken = default)
    {
        var allOutcomes = await GetAllOutcomesAsync(cancellationToken);
        return allOutcomes
            .Where(o => o.OutcomeLabeledUtc.Date == dateUtc.ToDateTime(TimeOnly.MinValue))
            .ToList();
    }

    public async Task<List<TradeOutcome>> GetAllOutcomesAsync(CancellationToken cancellationToken = default)
    {
        var outcomes = new List<TradeOutcome>();

        if (!File.Exists(_filePath))
        {
            _logger.Debug("Outcome store file not found at {FilePath}, returning empty list", _filePath);
            return outcomes;
        }

        try
        {
            using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string? line;
            int lineNumber = 0;

            while ((line = await reader.ReadLineAsync()) != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lineNumber++;

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var outcome = JsonSerializer.Deserialize<TradeOutcome>(line, _jsonOptions);
                    if (outcome != null)
                    {
                        outcomes.Add(outcome);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to deserialize outcome at line {LineNumber} in {FilePath}", lineNumber, _filePath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to read outcomes from {FilePath}", _filePath);
            throw;
        }

        _logger.Information("Loaded {Count} outcomes from {FilePath}", outcomes.Count, _filePath);
        return outcomes;
    }

    public async Task<List<TradeOutcome>> GetOutcomesByDateRangeAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default)
    {
        var allOutcomes = await GetAllOutcomesAsync(cancellationToken);
        return allOutcomes
            .Where(o => o.OutcomeLabeledUtc.Date >= startDate.ToDateTime(TimeOnly.MinValue) 
                     && o.OutcomeLabeledUtc.Date <= endDate.ToDateTime(TimeOnly.MaxValue))
            .ToList();
    }
}
