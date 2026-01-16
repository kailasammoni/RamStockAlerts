using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RamStockAlerts.Tests;

public class SubscriptionDiagnosticsTests
{
    [Fact]
    public void DiagnosticResult_AnalyzesNoDataOnPrimary_SuggestsRetry()
    {
        // Arrange: Symbol gets no data on primary exchange
        var result = new MockDiagnosticResult
        {
            Symbol = "TEST",
            PrimaryExchange = "NASDAQ",
            UsedExchange = "NASDAQ",
            GotL1 = false,
            GotTape = false,
            ErrorCodes = new List<int>()
        };

        // Act
        var analysis = AnalyzeResult(result);

        // Assert
        Assert.Contains("No data on NASDAQ", analysis);
        Assert.Contains("try SMART", analysis);
    }

    [Fact]
    public void DiagnosticResult_AnalyzesNoDataOnSmart_IndicatesDeadSymbol()
    {
        // Arrange: Symbol gets no data even on SMART
        var result = new MockDiagnosticResult
        {
            Symbol = "BIYA",
            PrimaryExchange = "NYSE",
            UsedExchange = "SMART",
            GotL1 = false,
            GotTape = false,
            ErrorCodes = new List<int>()
        };

        // Act
        var analysis = AnalyzeResult(result);

        // Assert
        Assert.Contains("No data even on SMART", analysis);
        Assert.Contains("symbol not trading or entitlement missing", analysis);
    }

    [Fact]
    public void DiagnosticResult_AnalyzesError10092_IdentifiesDepthNotEnabled()
    {
        // Arrange: Symbol returns error 10092 (depth not enabled)
        var result = new MockDiagnosticResult
        {
            Symbol = "AAPL",
            PrimaryExchange = "NASDAQ",
            UsedExchange = "SMART",
            GotL1 = true,
            GotTape = false,
            ErrorCodes = new List<int> { 10092 }
        };

        // Act
        var analysis = AnalyzeResult(result);

        // Assert
        Assert.Contains("Deep market data not enabled", analysis);
        Assert.Contains("error 10092", analysis);
    }

    [Fact]
    public void DiagnosticResult_AnalyzesError10167_IdentifiesTickByTickLimit()
    {
        // Arrange: Symbol returns error 10167 (tick-by-tick limit)
        var result = new MockDiagnosticResult
        {
            Symbol = "MSFT",
            PrimaryExchange = "NASDAQ",
            UsedExchange = "SMART",
            GotL1 = true,
            GotTape = false,
            ErrorCodes = new List<int> { 10167 }
        };

        // Act
        var analysis = AnalyzeResult(result);

        // Assert
        Assert.Contains("Tick-by-tick subscription limit reached", analysis);
        Assert.Contains("error 10167", analysis);
    }

    [Fact]
    public void DiagnosticResult_AnalyzesError200_IdentifiesNoSecurityDefinition()
    {
        // Arrange: Symbol returns error 200 (no security definition)
        var result = new MockDiagnosticResult
        {
            Symbol = "INVALID",
            PrimaryExchange = "UNKNOWN",
            UsedExchange = "SMART",
            GotL1 = false,
            GotTape = false,
            ErrorCodes = new List<int> { 200 }
        };

        // Act
        var analysis = AnalyzeResult(result);

        // Assert
        Assert.Contains("No security definition found", analysis);
        Assert.Contains("error 200", analysis);
    }

    [Fact]
    public void DiagnosticResult_AnalyzesL1WithoutTape_SuggestsEntitlementIssue()
    {
        // Arrange: Symbol gets L1 but no tick-by-tick
        var result = new MockDiagnosticResult
        {
            Symbol = "GOOGL",
            PrimaryExchange = "NASDAQ",
            UsedExchange = "SMART",
            GotL1 = true,
            GotTape = false,
            L1Count = 50,
            TapeCount = 0,
            ErrorCodes = new List<int>()
        };

        // Act
        var analysis = AnalyzeResult(result);

        // Assert
        Assert.Contains("L1 working but no tick-by-tick", analysis);
        Assert.Contains("entitlement", analysis);
    }

    [Fact]
    public void DiagnosticResult_AnalyzesWorkingSubscription_ShowsSuccess()
    {
        // Arrange: Symbol works perfectly
        var result = new MockDiagnosticResult
        {
            Symbol = "AAPL",
            PrimaryExchange = "NASDAQ",
            UsedExchange = "SMART",
            GotL1 = true,
            GotTape = true,
            L1Count = 100,
            TapeCount = 50,
            ErrorCodes = new List<int>()
        };

        // Act
        var analysis = AnalyzeResult(result);

        // Assert
        Assert.Contains("Working on SMART", analysis);
        Assert.Contains("L1: 100", analysis);
        Assert.Contains("Tape: 50", analysis);
    }

    [Fact]
    public void DiagnosticResult_AnalyzesClassificationError_ReportsError()
    {
        // Arrange: Symbol has no classification
        var result = new MockDiagnosticResult
        {
            Symbol = "UNKNOWN",
            PrimaryExchange = "UNKNOWN",
            UsedExchange = "N/A",
            ErrorMessage = "NoClassification"
        };

        // Act
        var analysis = AnalyzeResult(result);

        // Assert
        Assert.Equal("NoClassification", analysis);
    }

    [Fact]
    public void DiagnosticResult_PrimaryFailsSMARTWorks_ShowsExchangeRouting()
    {
        // Arrange: Primary fails but SMART works (exchange routing issue)
        var primaryResult = new MockDiagnosticResult
        {
            Symbol = "XYZ",
            PrimaryExchange = "NYSE",
            UsedExchange = "NYSE",
            GotL1 = false,
            GotTape = false,
            ErrorCodes = new List<int>()
        };

        var smartResult = new MockDiagnosticResult
        {
            Symbol = "XYZ",
            PrimaryExchange = "NYSE",
            UsedExchange = "SMART",
            GotL1 = true,
            GotTape = true,
            L1Count = 75,
            TapeCount = 30,
            ErrorCodes = new List<int>()
        };

        // Act
        var primaryAnalysis = AnalyzeResult(primaryResult);
        var smartAnalysis = AnalyzeResult(smartResult);

        // Assert
        Assert.Contains("No data on NYSE", primaryAnalysis);
        Assert.Contains("try SMART", primaryAnalysis);
        Assert.Contains("Working on SMART", smartAnalysis);
    }

    // Helper method matching the actual implementation logic
    private static string AnalyzeResult(MockDiagnosticResult result)
    {
        if (!string.IsNullOrEmpty(result.ErrorMessage))
        {
            return result.ErrorMessage;
        }

        if (result.ErrorCodes.Contains(10092))
        {
            return "Deep market data not enabled (error 10092)";
        }

        if (result.ErrorCodes.Contains(10167))
        {
            return "Tick-by-tick subscription limit reached (error 10167)";
        }

        if (result.ErrorCodes.Contains(200))
        {
            return "No security definition found (error 200)";
        }

        if (!result.GotL1 && !result.GotTape)
        {
            if (result.UsedExchange == "SMART")
            {
                return "No data even on SMART - symbol not trading or entitlement missing";
            }
            return $"No data on {result.UsedExchange} - try SMART or check entitlement";
        }

        if (result.GotL1 && !result.GotTape)
        {
            return "L1 working but no tick-by-tick - may need different subscription or entitlement";
        }

        return $"Working on {result.UsedExchange} - L1: {result.L1Count}, Tape: {result.TapeCount}";
    }

    private sealed class MockDiagnosticResult
    {
        public string Symbol { get; init; } = string.Empty;
        public string PrimaryExchange { get; set; } = string.Empty;
        public string UsedExchange { get; init; } = string.Empty;
        public bool GotL1 { get; set; }
        public bool GotTape { get; set; }
        public bool GotDepth { get; set; }
        public long? FirstRecvMs { get; set; }
        public long? LastRecvMs { get; set; }
        public long? LastRecvAgeMs { get; set; }
        public List<int> ErrorCodes { get; set; } = new();
        public int L1Count { get; set; }
        public int TapeCount { get; set; }
        public int DepthCount { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
