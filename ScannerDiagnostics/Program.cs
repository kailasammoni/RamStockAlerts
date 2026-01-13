using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace ScannerDiagnostics;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || (args[0] != "dump-params" && args[0] != "run-matrix"))
        {
            Console.WriteLine("Usage: dotnet run --project ScannerDiagnostics -- dump-params|run-matrix");
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

        var indicators = new[]
        {
            details.StockType,
            details.Category,
            details.Subcategory,
            details.LongName,
            details.DescAppend
        };

        if (indicators.Any(v => ContainsEtfIndicator(v)))
        {
            return "ETF";
        }

        if (string.Equals(details.SecType, "STK", StringComparison.OrdinalIgnoreCase))
        {
            return "CommonStock";
        }

        return "Unknown";
    }

    private static bool ContainsEtfIndicator(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("ETF", StringComparison.OrdinalIgnoreCase)
               || value.Contains("ETN", StringComparison.OrdinalIgnoreCase)
               || value.Contains("ETP", StringComparison.OrdinalIgnoreCase)
               || value.Contains("FUND", StringComparison.OrdinalIgnoreCase);
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
}

public sealed record ScanConfig
{
    public string Name { get; init; } = "default";
    public string Instrument { get; init; } = "STK";
    public string LocationCode { get; init; } = "STK.US.MAJOR";
    public string ScanCode { get; init; } = "MOST_ACTIVE";
    public string? StockTypeFilter { get; init; }
}

public sealed record ScanSummary(string Name, int Total, int Etf, int Common, int Unknown);

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
