using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;

namespace RamStockAlerts.Services;

public sealed class ScarcityController
{
    private readonly int _maxBlueprintsPerDay;
    private readonly int _maxPerSymbolPerDay;
    private readonly int _globalCooldownMinutes;
    private readonly int _perSymbolCooldownMinutes;
    private readonly int _rankWindowSeconds;
    private readonly List<RankWindowCandidate> _rankWindowCandidates = new();
    private long? _rankWindowBucket;
    private long _rankSequence;

    private DateOnly _currentDay = DateOnly.FromDateTime(DateTime.UtcNow);
    private int _acceptedCountToday = 0;
    private readonly Dictionary<string, int> _acceptedPerSymbolToday = new(StringComparer.OrdinalIgnoreCase);
    private long _lastAcceptedTimestampGlobal;
    private readonly Dictionary<string, long> _lastAcceptedTimestampPerSymbol = new(StringComparer.OrdinalIgnoreCase);

    public ScarcityController(IConfiguration configuration)
    {
        var section = configuration.GetSection("Scarcity");
        _maxBlueprintsPerDay = section.GetValue("MaxBlueprintsPerDay", 6);
        _maxPerSymbolPerDay = section.GetValue("MaxPerSymbolPerDay", 1);
        _globalCooldownMinutes = section.GetValue("GlobalCooldownMinutes", 45);
        _perSymbolCooldownMinutes = section.GetValue("PerSymbolCooldownMinutes",
            section.GetValue("SymbolCooldownMinutes", 9999));
        _rankWindowSeconds = section.GetValue("RankWindowSeconds", 0);
    }

    public ScarcityDecision Evaluate(string symbol, decimal score, long timestampMsUtc)
    {
        return EvaluateInternal(symbol, score, timestampMsUtc);
    }

    public IReadOnlyList<RankedScarcityDecision> StageCandidate(Guid candidateId, string symbol, decimal score, long timestampMsUtc)
    {
        if (_rankWindowSeconds <= 0)
        {
            var decision = EvaluateInternal(symbol, score, timestampMsUtc);
            if (decision.Accepted)
            {
                RecordAcceptance(symbol, timestampMsUtc);
            }

            return new[] { new RankedScarcityDecision(candidateId, decision) };
        }

        var bucket = GetBucket(timestampMsUtc);
        var decisions = new List<RankedScarcityDecision>();

        if (_rankWindowBucket == null)
        {
            _rankWindowBucket = bucket;
        }
        else if (bucket != _rankWindowBucket)
        {
            decisions.AddRange(FlushCurrentBucket());
            _rankWindowBucket = bucket;
        }

        _rankWindowCandidates.Add(new RankWindowCandidate(candidateId, symbol, score, timestampMsUtc, _rankSequence++));
        return decisions;
    }

    public IReadOnlyList<RankedScarcityDecision> FlushRankWindow(long currentTimestampMsUtc, bool force = false)
    {
        if (_rankWindowSeconds <= 0 || _rankWindowBucket == null)
        {
            return Array.Empty<RankedScarcityDecision>();
        }

        var currentBucket = GetBucket(currentTimestampMsUtc);
        if (!force && currentBucket == _rankWindowBucket)
        {
            return Array.Empty<RankedScarcityDecision>();
        }

        var decisions = FlushCurrentBucket();
        _rankWindowBucket = null;
        return decisions;
    }

    public void RecordAcceptance(string symbol, long timestampMsUtc)
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(timestampMsUtc).UtcDateTime;
        EnsureDay(now);

        _acceptedCountToday++;
        _acceptedPerSymbolToday.TryGetValue(symbol, out var symbolCount);
        _acceptedPerSymbolToday[symbol] = symbolCount + 1;
        _lastAcceptedTimestampGlobal = timestampMsUtc;
        _lastAcceptedTimestampPerSymbol[symbol] = timestampMsUtc;
    }

    private ScarcityDecision EvaluateInternal(string symbol, decimal score, long timestampMsUtc)
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(timestampMsUtc).UtcDateTime;
        EnsureDay(now);

        if (_acceptedCountToday >= _maxBlueprintsPerDay)
        {
            return new ScarcityDecision(false, "GlobalLimit", $"Daily limit {_maxBlueprintsPerDay} reached");
        }

        if (_globalCooldownMinutes > 0)
        {
            var elapsed = timestampMsUtc - _lastAcceptedTimestampGlobal;
            if (_lastAcceptedTimestampGlobal > 0 && elapsed < _globalCooldownMinutes * 60_000)
            {
                var remaining = (_globalCooldownMinutes * 60_000 - elapsed) / 1000.0;
                return new ScarcityDecision(false, "GlobalCooldown", $"{remaining:F1}s remaining");
            }
        }

        if (_perSymbolCooldownMinutes > 0)
        {
            if (_lastAcceptedTimestampPerSymbol.TryGetValue(symbol, out var lastSymbolTs))
            {
                var elapsed = timestampMsUtc - lastSymbolTs;
                if (elapsed < _perSymbolCooldownMinutes * 60_000)
                {
                    var remaining = (_perSymbolCooldownMinutes * 60_000 - elapsed) / 1000.0;
                    return new ScarcityDecision(false, "SymbolCooldown", $"{remaining:F1}s remaining");
                }
            }
        }

        var symbolCount = _acceptedPerSymbolToday.TryGetValue(symbol, out var count) ? count : 0;
        if (symbolCount >= _maxPerSymbolPerDay)
        {
            return new ScarcityDecision(false, "SymbolLimit", $"Symbol {symbol} limit {_maxPerSymbolPerDay} reached");
        }

        return new ScarcityDecision(true, "Accepted", "Scarcity OK");
    }

    private IReadOnlyList<RankedScarcityDecision> FlushCurrentBucket()
    {
        if (_rankWindowCandidates.Count == 0)
        {
            return Array.Empty<RankedScarcityDecision>();
        }

        var ordered = _rankWindowCandidates
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.TimestampMsUtc)
            .ThenBy(c => c.Sequence)
            .ThenBy(c => c.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var decisions = new List<RankedScarcityDecision>(ordered.Count);
        var halted = false;

        foreach (var candidate in ordered)
        {
            if (halted)
            {
                decisions.Add(new RankedScarcityDecision(
                    candidate.CandidateId,
                    new ScarcityDecision(false, "RejectedRankedOut", $"Ranked out in {_rankWindowSeconds}s window")));
                continue;
            }

            var decision = EvaluateInternal(candidate.Symbol, candidate.Score, candidate.TimestampMsUtc);
            if (decision.Accepted)
            {
                RecordAcceptance(candidate.Symbol, candidate.TimestampMsUtc);
                decisions.Add(new RankedScarcityDecision(candidate.CandidateId, decision));
            }
            else
            {
                decisions.Add(new RankedScarcityDecision(candidate.CandidateId, decision));
                halted = true;
            }
        }

        _rankWindowCandidates.Clear();
        return decisions;
    }

    private long GetBucket(long timestampMsUtc) => timestampMsUtc / (_rankWindowSeconds * 1000L);

    private void EnsureDay(DateTime utcNow)
    {
        var today = DateOnly.FromDateTime(utcNow);
        if (today != _currentDay)
        {
            _currentDay = today;
            _acceptedCountToday = 0;
            _acceptedPerSymbolToday.Clear();
        }
    }
}

public sealed record ScarcityDecision(bool Accepted, string ReasonCode, string ReasonDetail);

public sealed record RankedScarcityDecision(Guid CandidateId, ScarcityDecision Decision);

internal sealed record RankWindowCandidate(
    Guid CandidateId,
    string Symbol,
    decimal Score,
    long TimestampMsUtc,
    long Sequence);
