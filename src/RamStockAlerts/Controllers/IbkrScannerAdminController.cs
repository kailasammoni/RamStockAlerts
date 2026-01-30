using Microsoft.AspNetCore.Mvc;
using RamStockAlerts.Universe;

namespace RamStockAlerts.Controllers;

[ApiController]
[Route("admin/ibkr/scanner")]
public sealed class IbkrScannerAdminController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly IbkrScannerUniverseSource _scannerSource;

    public IbkrScannerAdminController(
        IConfiguration configuration,
        IbkrScannerUniverseSource scannerSource)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _scannerSource = scannerSource ?? throw new ArgumentNullException(nameof(scannerSource));
    }

    /// <summary>
    /// Runs an on-demand IBKR scanner request (useful for validating TWS vs IB Gateway settings).
    /// </summary>
    [HttpPost("run")]
    [ProducesResponseType(typeof(IbkrScannerRunResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Run([FromBody] IbkrScannerRunRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return BadRequest(new { error = "Request body is required." });
        }

        var host = string.IsNullOrWhiteSpace(request.Host)
            ? (_configuration["IBKR:Host"] ?? "127.0.0.1")
            : request.Host.Trim();
        var port = request.Port
            ?? _configuration.GetValue<int?>("IBKR:Port")
            ?? 7496;

        var baseClientId = _configuration.GetValue<int?>("IBKR:ClientId") ?? 1;
        var defaultScannerClientId = _configuration.GetValue<int?>("Universe:IbkrScanner:ClientId") ?? baseClientId + 9;
        var clientId = request.ClientId ?? defaultScannerClientId;

        var instrument = string.IsNullOrWhiteSpace(request.Instrument)
            ? (_configuration["Universe:IbkrScanner:Instrument"] ?? "STK")
            : request.Instrument.Trim();
        var locationCode = string.IsNullOrWhiteSpace(request.LocationCode)
            ? (_configuration["Universe:IbkrScanner:LocationCode"] ?? "STK.US.MAJOR")
            : request.LocationCode.Trim();

        var scanCodes = (request.ScanCodes ?? new List<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (scanCodes.Count == 0 && !string.IsNullOrWhiteSpace(request.ScanCode))
        {
            scanCodes.Add(request.ScanCode.Trim());
        }
        if (scanCodes.Count == 0)
        {
            scanCodes = _configuration
                .GetSection("Universe:IbkrScanner:ScanCodes")
                .GetChildren()
                .Select(x => x.Value)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        if (scanCodes.Count == 0)
        {
            return BadRequest(new { error = "No ScanCodes provided and none configured at Universe:IbkrScanner:ScanCodes." });
        }

        var rows = request.Rows
            ?? _configuration.GetValue<int?>("Universe:IbkrScanner:Rows")
            ?? 50;
        var abovePrice = request.AbovePrice
            ?? _configuration.GetValue<double?>("Universe:IbkrScanner:AbovePrice")
            ?? 0.0;
        var belowPrice = request.BelowPrice
            ?? _configuration.GetValue<double?>("Universe:IbkrScanner:BelowPrice")
            ?? 0.0;
        var aboveVolume = request.AboveVolume
            ?? _configuration.GetValue<int?>("Universe:IbkrScanner:AboveVolume")
            ?? 0;

        var configuredFloatSharesBelow = _configuration.GetValue<double?>("Universe:IbkrScanner:FloatSharesBelow");
        var effectiveFloatSharesBelow = request.DisableFloatFilter
            ? null
            : request.FloatSharesBelow ?? configuredFloatSharesBelow;

        var startedUtc = DateTimeOffset.UtcNow;
        var results = new List<IbkrScannerRunItem>();

        foreach (var scanCode in scanCodes)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var symbols = await _scannerSource.ExecuteScannerAttemptAsync(
                    host,
                    port,
                    clientId,
                    instrument,
                    locationCode,
                    scanCode,
                    rows,
                    abovePrice,
                    belowPrice,
                    aboveVolume,
                    effectiveFloatSharesBelow,
                    marketCapAbove: null,
                    marketCapBelow: null,
                    cancellationToken: ct);

                results.Add(new IbkrScannerRunItem
                {
                    ScanCode = scanCode,
                    SymbolCount = symbols.Count,
                    Symbols = symbols.ToArray(),
                    Error = null
                });
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                results.Add(new IbkrScannerRunItem
                {
                    ScanCode = scanCode,
                    SymbolCount = 0,
                    Symbols = Array.Empty<string>(),
                    Error = ex.Message
                });
            }
        }

        var response = new IbkrScannerRunResponse
        {
            Host = host,
            Port = port,
            ClientId = clientId,
            Instrument = instrument,
            LocationCode = locationCode,
            Rows = rows,
            AbovePrice = abovePrice,
            BelowPrice = belowPrice,
            AboveVolume = aboveVolume,
            FloatSharesBelow = effectiveFloatSharesBelow,
            DisableFloatFilter = request.DisableFloatFilter,
            ScanCodes = scanCodes.ToArray(),
            StartedUtc = startedUtc,
            CompletedUtc = DateTimeOffset.UtcNow,
            Results = results.ToArray()
        };

        return Ok(response);
    }
}

public sealed class IbkrScannerRunRequest
{
    public string? Host { get; set; }
    public int? Port { get; set; }
    public int? ClientId { get; set; }

    public string? Instrument { get; set; }
    public string? LocationCode { get; set; }

    public string? ScanCode { get; set; }
    public List<string>? ScanCodes { get; set; }

    public int? Rows { get; set; }
    public double? AbovePrice { get; set; }
    public double? BelowPrice { get; set; }
    public int? AboveVolume { get; set; }

    public bool DisableFloatFilter { get; set; }
    public double? FloatSharesBelow { get; set; }
}

public sealed class IbkrScannerRunResponse
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public int ClientId { get; set; }

    public string Instrument { get; set; } = string.Empty;
    public string LocationCode { get; set; } = string.Empty;
    public string[] ScanCodes { get; set; } = Array.Empty<string>();

    public int Rows { get; set; }
    public double AbovePrice { get; set; }
    public double BelowPrice { get; set; }
    public int AboveVolume { get; set; }

    public bool DisableFloatFilter { get; set; }
    public double? FloatSharesBelow { get; set; }

    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset CompletedUtc { get; set; }

    public IbkrScannerRunItem[] Results { get; set; } = Array.Empty<IbkrScannerRunItem>();
}

public sealed class IbkrScannerRunItem
{
    public string ScanCode { get; set; } = string.Empty;
    public int SymbolCount { get; set; }
    public string[] Symbols { get; set; } = Array.Empty<string>();
    public string? Error { get; set; }
}

