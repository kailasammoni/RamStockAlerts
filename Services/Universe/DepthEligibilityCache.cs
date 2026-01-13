using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace RamStockAlerts.Services.Universe;

public enum DepthEligibilityStatus
{
    Unknown,
    Eligible,
    Ineligible
}

public sealed record DepthEligibilityState(
    DepthEligibilityStatus Status,
    string? Reason,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CooldownUntil,
    int? ConId,
    string? Symbol,
    string? PrimaryExchange);

public sealed class DepthEligibilityCache
{
    private readonly ConcurrentDictionary<string, DepthEligibilityState> _states = new();
    private readonly HashSet<string> _skipLogs = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _eligibleTtl;
    private readonly TimeSpan _ineligibleTtl;
    private readonly ILogger<DepthEligibilityCache> _logger;

    public DepthEligibilityCache(IConfiguration configuration, ILogger<DepthEligibilityCache> logger)
    {
        _logger = logger;
        var hours = Math.Max(1, configuration.GetValue("MarketData:DepthEligibilityTtlHours", 24));
        _eligibleTtl = TimeSpan.FromHours(hours);
        _ineligibleTtl = TimeSpan.FromHours(hours);
    }

    public DepthEligibilityState Get(
        ContractClassification? classification,
        string symbol,
        DateTimeOffset asOf)
    {
        if (!TryGetKey(classification, symbol, out var key))
        {
            return new DepthEligibilityState(DepthEligibilityStatus.Unknown, null, DateTimeOffset.MinValue, null, null, symbol, null);
        }

        if (_states.TryGetValue(key, out var state))
        {
            if (!IsExpired(state, asOf))
            {
                return state;
            }

            _states.TryRemove(key, out _);
        }

        return new DepthEligibilityState(DepthEligibilityStatus.Unknown, null, DateTimeOffset.MinValue, null, classification?.ConId, symbol, classification?.PrimaryExchange);
    }

    public bool CanRequestDepth(
        ContractClassification? classification,
        string symbol,
        DateTimeOffset asOf,
        out DepthEligibilityState state)
    {
        state = Get(classification, symbol, asOf);
        if (state.Status != DepthEligibilityStatus.Ineligible)
        {
            return true;
        }

        if (state.CooldownUntil.HasValue && state.CooldownUntil.Value > asOf)
        {
            return false;
        }

        return true;
    }

    public void MarkEligible(ContractClassification? classification, string symbol)
    {
        if (!TryGetKey(classification, symbol, out var key))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        _states[key] = new DepthEligibilityState(
            DepthEligibilityStatus.Eligible,
            null,
            now,
            now.Add(_eligibleTtl),
            classification?.ConId,
            symbol.Trim().ToUpperInvariant(),
            classification?.PrimaryExchange);
    }

    public void MarkIneligible(
        ContractClassification? classification,
        string symbol,
        string reason,
        DateTimeOffset? cooldownUntil)
    {
        if (!TryGetKey(classification, symbol, out var key))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var until = cooldownUntil ?? now.Add(_ineligibleTtl);
        _states[key] = new DepthEligibilityState(
            DepthEligibilityStatus.Ineligible,
            reason,
            now,
            until,
            classification?.ConId,
            symbol.Trim().ToUpperInvariant(),
            classification?.PrimaryExchange);
    }

    public void LogSkipOnce(ContractClassification? classification, string symbol, DepthEligibilityState state)
    {
        if (!TryGetKey(classification, symbol, out var key))
        {
            return;
        }

        if (_skipLogs.Add(key))
        {
            _logger.LogInformation(
                "SkipDepth {Symbol} reason={Reason} until={Until}",
                symbol,
                state.Reason ?? "Unknown",
                state.CooldownUntil?.ToString("O") ?? "n/a");
        }
    }

    private bool TryGetKey(ContractClassification? classification, string symbol, out string key)
    {
        key = string.Empty;
        if (classification?.ConId is > 0)
        {
            key = $"conid:{classification.ConId}";
            return true;
        }

        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        var normalized = symbol.Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(classification?.PrimaryExchange))
        {
            key = $"symbol:{normalized}@{classification.PrimaryExchange}";
        }
        else
        {
            key = $"symbol:{normalized}";
        }

        return true;
    }

    private bool IsExpired(DepthEligibilityState state, DateTimeOffset asOf)
    {
        var ttl = state.Status == DepthEligibilityStatus.Eligible ? _eligibleTtl : _ineligibleTtl;
        return asOf - state.UpdatedAt > ttl;
    }
}
