using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace ScannerDiagnostics;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || (args[0] != "dump-params" && args[0] != "run-matrix" && args[0] != "run-most-active-major" && args[0] != "run-float-low" && args[0] != "test-subscriptions"))
        {
            Console.WriteLine("Usage: dotnet run --project ScannerDiagnostics -- dump-params|run-matrix|run-most-active-major|run-float-low|test-subscriptions");
            return 1;
        }

        var configRoot = BuildConfiguration();
        var ibkrSection = configRoot.GetSection("Ibkr");
        var diagSection = configRoot.GetSection("Diagnostics");
        var ibkr = ibkrSection.Exists() ? ibkrSection.Get<IbkrConfig>() ?? new IbkrConfig() : new IbkrConfig();
        var diag = diagSection.Exists() ? diagSection.Get<DiagnosticsConfig>() ?? new DiagnosticsConfig() : new DiagnosticsConfig();
        var scanConfigs = configRoot.GetSection("ScanMatrix").Get<List<ScanConfig>>() ?? new List<ScanConfig>();

        var artifactsDir = Path.Combine(AppContext.BaseDirectory, "artifacts");
        Directory.CreateDirectory(artifactsDir);

        using var client = new ScannerClient(ibkr);
        try
        {
            await client.ConnectAsync(TimeSpan.FromMilliseconds(diag.RequestTimeoutMs));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Connect failed: {ex.Message}");
            return 1;
        }

        try
        {
            switch (args[0])
            {
                case "dump-params":
                    await DumpParamsAsync(client, artifactsDir, diag);
                    break;
                case "run-matrix":
                    if (scanConfigs.Count == 0)
                    {
                        Console.WriteLine("No ScanMatrix entries configured.");
                        return 1;
                    }

                    await RunMatrixAsync(client, scanConfigs, artifactsDir, diag);
                    break;
                case "run-most-active-major":
                    await RunMostActiveMajorAsync(client, artifactsDir, diag);
                    break;
                case "run-float-low":
                    await RunFloatLowAsync(client, artifactsDir, diag);
                    break;
                case "test-subscriptions":
                    await TestSubscriptionsAsync(artifactsDir, diag, ibkr);
                    return 0;
            }
        }
        finally
        {
            await client.DisconnectAsync();
        }

        return 0;
    }

    private static IConfiguration BuildConfiguration()
    {
        return new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .Build();
    }

    private static async Task DumpParamsAsync(ScannerClient client, string artifactsDir, DiagnosticsConfig diag)
    {
        var timeout = TimeSpan.FromMilliseconds(diag.RequestTimeoutMs);
        Console.WriteLine("Requesting scanner parameters...");
        var xml = await client.GetScannerParametersAsync(timeout);
        var outPath = Path.Combine(artifactsDir, "scanner-params.xml");
        await File.WriteAllTextAsync(outPath, xml);
        Console.WriteLine($"Saved parameters -> {outPath}");

        var summary = ScannerParamsParser.Parse(xml);
        Console.WriteLine("Summary:");
        Console.WriteLine($"  Instruments (contains STK): {string.Join(", ", summary.Instruments.Take(10))}");
        Console.WriteLine($"  Location codes (STK.US*): {string.Join(", ", summary.Locations.Take(10))}");
        Console.WriteLine($"  StockTypeFilter supported: {summary.StockTypeFilterSupported}");
        if (summary.StockTypeValues.Count > 0)
        {
            Console.WriteLine($"  StockTypeFilter values: {string.Join(", ", summary.StockTypeValues)}");
        }
    }

    private static async Task RunMatrixAsync(
        ScannerClient client,
        IReadOnlyCollection<ScanConfig> configs,
        string artifactsDir,
        DiagnosticsConfig diag)
    {
        var summaries = new List<ScanSummary>();
        foreach (var config in configs)
        {
            Console.WriteLine($"Running scan {config.Name} ({config.ScanCode} {config.LocationCode})...");
            var timeout = TimeSpan.FromMilliseconds(diag.RequestTimeoutMs);

            var scanResult = await client.RunScanAsync(config, diag, timeout);
            var enriched = await EnrichAsync(client, scanResult.Rows, diag);

            var summary = BuildSummary(config, enriched);
            summaries.Add(summary);

            var payload = new
            {
                Config = config,
                summary,
                Results = enriched
            };

            var outPath = Path.Combine(artifactsDir, $"scan-{SanitizeFileName(config.Name)}.json");
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
            await File.WriteAllTextAsync(outPath, json);

            Console.WriteLine($"  total={summary.Total} etf={summary.Etf} common={summary.Common} unknown={summary.Unknown} -> {outPath}");
        }

        Console.WriteLine();
        Console.WriteLine("Summary table:");
        foreach (var s in summaries)
        {
            Console.WriteLine($"  {s.Name,-20} total={s.Total,3} etf={s.Etf,3} common={s.Common,3} unknown={s.Unknown,3}");
        }
    }

    private static async Task RunMostActiveMajorAsync(ScannerClient client, string artifactsDir, DiagnosticsConfig diag)
    {
        var config = new ScanConfig
        {
            Name = "MostActiveMajor",
            Instrument = "STK",
            LocationCode = "STK.US.MAJOR",
            ScanCode = "MOST_ACTIVE"
        };

        var timeout = TimeSpan.FromMilliseconds(diag.RequestTimeoutMs);
        Console.WriteLine("Running MOST_ACTIVE STK.US.MAJOR...");
        await RunAndSummarizeAsync(client, config, artifactsDir, diag, "scan-most-active-major.json");
    }

    private static async Task RunFloatLowAsync(ScannerClient client, string artifactsDir, DiagnosticsConfig diag)
    {
        var config = new ScanConfig
        {
            Name = "FloatLow",
            Instrument = "STK",
            LocationCode = "STK.US.MAJOR",
            ScanCode = "SCAN_floatShares_ASC",
            FloatSharesBelow = diag.FloatSharesBelow ?? 150_000_000m,
            PriceAbove = diag.PriceAbove ?? 5m,
            PriceBelow = diag.PriceBelow ?? 20m,
            VolumeAbove = diag.VolumeAbove ?? 500_000m
        };

        var timeout = TimeSpan.FromMilliseconds(diag.RequestTimeoutMs);
        Console.WriteLine($"Running SCAN_floatShares_ASC (price {config.PriceAbove}-{config.PriceBelow}, float<{config.FloatSharesBelow}, vol>{config.VolumeAbove})...");
        await RunAndSummarizeAsync(client, config, artifactsDir, diag, "scan-float-low.json");
    }

    private static async Task RunAndSummarizeAsync(
        ScannerClient client,
        ScanConfig config,
        string artifactsDir,
        DiagnosticsConfig diag,
        string fileName)
    {
        var timeout = TimeSpan.FromMilliseconds(diag.RequestTimeoutMs);
        var scanResult = await client.RunScanAsync(config, diag, timeout);
        var enriched = await EnrichAsync(client, scanResult.Rows, diag);

        var classified = enriched.Select(row =>
        {
            var classification = Classify(row.Details);
            var exclusion = EvaluateExclusion(row, classification);
            return new RowWithClassification(row, classification, exclusion);
        }).ToList();

        var rawSummary = BuildStockTypeSummary(classified);
        var filtered = classified.Where(c => c.ExclusionReason is null).ToList();
        var filteredSummary = BuildStockTypeSummary(filtered);

        var excludedGroups = classified
            .Where(c => c.ExclusionReason is not null)
            .GroupBy(c => c.ExclusionReason!)
            .ToDictionary(g => g.Key, g => g.Count());

        Console.WriteLine($"Raw: total={rawSummary.Total} common={rawSummary.Common} etf={rawSummary.Etf} etn={rawSummary.Etn} unknown={rawSummary.Unknown} other={rawSummary.Other}");
        Console.WriteLine($"Excluded by reason: {string.Join(", ", excludedGroups.Select(kv => $"{kv.Key}={kv.Value}"))}");
        Console.WriteLine($"Final: total={filteredSummary.Total} top10={string.Join(", ", filtered.Take(10).Select(r => r.Row.Row.Symbol))}");

        var payload = new
        {
            Config = config,
            Raw = rawSummary,
            Excluded = excludedGroups,
            Final = filteredSummary,
            Results = classified
        };

        var outPath = Path.Combine(artifactsDir, fileName);
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
        await File.WriteAllTextAsync(outPath, json);
        Console.WriteLine($"Saved artifact -> {outPath}");
    }

    private static async Task<List<EnrichedRow>> EnrichAsync(
        ScannerClient client,
        IReadOnlyList<ScannerRow> rows,
        DiagnosticsConfig diag)
    {
        var enriched = new List<EnrichedRow>();
        var throttle = TimeSpan.FromMilliseconds(Math.Max(1, diag.ContractDetailsThrottleMs));
        DateTimeOffset lastRequest = DateTimeOffset.MinValue;

        foreach (var row in rows.Take(diag.MaxRows))
        {
            var wait = throttle - (DateTimeOffset.UtcNow - lastRequest);
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait);
            }

            lastRequest = DateTimeOffset.UtcNow;
            ContractDetailsInfo? details = null;
            try
            {
                details = await client.GetContractDetailsAsync(row.Symbol, row.SecType, diag);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    contract details failed for {row.Symbol}: {ex.Message}");
            }

            enriched.Add(new EnrichedRow(row, details, Classify(details)));
        }

        return enriched;
    }

    private static string Classify(ContractDetailsInfo? details)
    {
        if (details == null)
        {
            return "Unknown";
        }

        var stockType = details.StockType?.Trim();
        if (!string.IsNullOrWhiteSpace(stockType))
        {
            if (stockType.Equals("COMMON", StringComparison.OrdinalIgnoreCase))
            {
                return "CommonStock";
            }
            if (stockType.Equals("ETF", StringComparison.OrdinalIgnoreCase))
            {
                return "Etf";
            }
            if (stockType.Equals("ETN", StringComparison.OrdinalIgnoreCase) || stockType.Equals("ETP", StringComparison.OrdinalIgnoreCase))
            {
                return "Etn";
            }
            return "Other";
        }

        return "Unknown";
    }

    private static string? EvaluateExclusion(EnrichedRow row, string classification)
    {
        if (row.Details is null)
        {
            return "MissingClassification";
        }

        if (!string.Equals(classification, "CommonStock", StringComparison.OrdinalIgnoreCase))
        {
            return $"StockTypeNotCommon:{row.Details.StockType ?? "null"}";
        }

        if (string.IsNullOrWhiteSpace(row.Details.PrimaryExchange))
        {
            return "MissingPrimaryExchange";
        }

        var exch = row.Details.PrimaryExchange.Trim().ToUpperInvariant();
        if (exch != "NYSE" && exch != "NASDAQ")
        {
            return $"PrimaryExchangeNotAllowed:{row.Details.PrimaryExchange}";
        }

        return null;
    }

    private static StockTypeSummary BuildStockTypeSummary(IReadOnlyCollection<RowWithClassification> rows)
    {
        var total = rows.Count;
        var etf = rows.Count(r => r.Classification == "Etf");
        var etn = rows.Count(r => r.Classification == "Etn");
        var common = rows.Count(r => r.Classification == "CommonStock");
        var unknown = rows.Count(r => r.Classification == "Unknown");
        var other = total - etf - etn - common - unknown;
        return new StockTypeSummary(total, etf, common, etn, unknown, other);
    }

    private static ScanSummary BuildSummary(ScanConfig config, IReadOnlyList<EnrichedRow> rows)
    {
        return new ScanSummary(
            config.Name,
            rows.Count,
            rows.Count(r => r.Classification == "ETF"),
            rows.Count(r => r.Classification == "CommonStock"),
            rows.Count(r => r.Classification == "Unknown"));
    }

    private static string SanitizeFileName(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(c, '_');
        }
        return value;
    }

    private static async Task TestSubscriptionsAsync(string artifactsDir, DiagnosticsConfig diag, IbkrConfig ibkr)
    {
        Console.WriteLine("\n╔════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║        IBKR Subscription Limits Diagnostic Test            ║");
        Console.WriteLine("║              (Common Stock Symbols Only)                   ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════╝\n");

        // Common Stock symbols only - filtered from high-conviction list
        var testSymbols = new[] { "AMD", "INTC", "NVDA", "CRM", "ADSK", "QCOM", "AVGO", "MCHP", "CDNS", "SNPS", "KLAC", "LRCX", "ASML", "NXPI", "STM", "MARA", "RIOT", "COIN", "TSLA", "PLTR" };

        var tool = new SubscriptionDiagnosticsTool(ibkr);

        try
        {
            var results = await tool.RunTestAsync(testSymbols, TimeSpan.FromSeconds(10));

            // Display results
            Console.WriteLine($"Test completed");
            Console.WriteLine($"Total symbols tested: {results.TotalSymbols}");
            Console.WriteLine($"Total errors: {results.Errors.Count}\n");

            var summary = results.Summary;
            Console.WriteLine("╔════ Subscription Tiers ════╗");
            Console.WriteLine($"║ Depth + Tick-by-Tick: {summary.DepthPlusTickByTickSuccess,3} ║");
            Console.WriteLine($"║ Depth Only:           {summary.DepthOnlySuccess,3} ║");
            Console.WriteLine($"║ Tick-by-Tick Only:    {summary.TickByTickOnlySuccess,3} ║");
            Console.WriteLine($"║ Tape Only (Fallback): {summary.TapeOnlyFallback,3} ║");
            Console.WriteLine("╚════════════════════════════╝\n");

            // Group errors by code
            if (results.Errors.Count > 0)
            {
                var errorsByCode = results.Errors.GroupBy(e => e.ErrorCode).ToList();
                Console.WriteLine("╔════ Error Codes ════╗");
                foreach (var group in errorsByCode.OrderBy(g => g.Key))
                {
                    Console.WriteLine($"║ Code {group.Key}: {group.Count()} errors");
                }
                Console.WriteLine("╚═══════════════════════╝\n");
            }

            // Show per-symbol results
            Console.WriteLine("╔════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║                     Per-Symbol Results                     ║");
            Console.WriteLine("╠════════════════════════════════════════════════════════════╣");

            foreach (var result in results.Results.OrderBy(s => s.Symbol))
            {
                var hasDepth = result.DepthDataReceived;
                var hasTick = result.TickByTickDataReceived;
                var icon = hasDepth && hasTick ? "✓✓" :
                          hasDepth ? "✓ " :
                          hasTick ? "✓ " :
                          "⚠ ";

                var tier = hasDepth && hasTick ? "Depth + Tick" :
                          hasDepth ? "Depth Only" :
                          hasTick ? "Tick Only" :
                          "Tape Only";

                var errorMsg = result.DepthErrorCode.HasValue ? $"[D:{result.DepthErrorCode}]" :
                              result.TickByTickErrorCode.HasValue ? $"[T:{result.TickByTickErrorCode}]" :
                              "";

                Console.WriteLine($"║ {icon} {result.Symbol,-6} → {tier,-17} {errorMsg,-20} ║");
            }
            Console.WriteLine("╚════════════════════════════════════════════════════════════╝");

            // Export JSON
            var outputPath = Path.Combine(artifactsDir, "subscription-test-results.json");
            var json = System.Text.Json.JsonSerializer.Serialize(results, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(outputPath, json);
            Console.WriteLine($"\nResults exported to: {outputPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during subscription test: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }
}

public sealed record IbkrConfig
{
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 7496;
    public int ClientId { get; init; } = 9901;
}

public sealed record DiagnosticsConfig
{
    public int MaxRows { get; init; } = 20;
    public int ContractDetailsThrottleMs { get; init; } = 1000;
    public int RequestTimeoutMs { get; init; } = 10_000;
    public decimal? FloatSharesBelow { get; init; } = 150_000_000m;
    public decimal? PriceAbove { get; init; } = 5m;
    public decimal? PriceBelow { get; init; } = 20m;
    public decimal? VolumeAbove { get; init; } = 500_000m;
}

public sealed record ScanConfig
{
    public string Name { get; init; } = "default";
    public string Instrument { get; init; } = "STK";
    public string LocationCode { get; init; } = "STK.US.MAJOR";
    public string ScanCode { get; init; } = "MOST_ACTIVE";
    public decimal? FloatSharesBelow { get; init; }
    public decimal? PriceAbove { get; init; }
    public decimal? PriceBelow { get; init; }
    public decimal? VolumeAbove { get; init; }
    public decimal? MarketCapAbove { get; init; }
    public decimal? MarketCapBelow { get; init; }
}

public sealed record ScanSummary(string Name, int Total, int Etf, int Common, int Unknown);
public sealed record StockTypeSummary(int Total, int Etf, int Common, int Etn, int Unknown, int Other);
public sealed record RowWithClassification(EnrichedRow Row, string Classification, string? ExclusionReason);

public sealed record ScannerRow(string Symbol, string SecType, int Rank);

public sealed record ContractDetailsInfo(
    string Symbol,
    string SecType,
    int ConId,
    string? Exchange,
    string? PrimaryExchange,
    string? Currency,
    string? LongName,
    string? StockType,
    string? Category,
    string? Subcategory,
    string? DescAppend);

public sealed record EnrichedRow(ScannerRow Row, ContractDetailsInfo? Details, string Classification);
