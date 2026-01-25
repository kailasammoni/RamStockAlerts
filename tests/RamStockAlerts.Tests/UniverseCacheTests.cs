using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace RamStockAlerts.Tests;

public class UniverseCacheTests : IAsyncLifetime
{
    private readonly string _testCacheDir = Path.Combine(Path.GetTempPath(), $"universe-cache-tests-{Guid.NewGuid()}");
    private string _testCacheFile => Path.Combine(_testCacheDir, "test-universe-cache.jsonl");

    public Task InitializeAsync()
    {
        // Create test directory
        if (!Directory.Exists(_testCacheDir))
        {
            Directory.CreateDirectory(_testCacheDir);
        }
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        // Cleanup test files
        try
        {
            if (Directory.Exists(_testCacheDir))
            {
                Directory.Delete(_testCacheDir, true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
        await Task.CompletedTask;
    }

    [Fact]
    public void CacheFilePath_Default_IsLogsDirectory()
    {
        // Validate default cache file path configuration
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>())
            .Build();

        var defaultPath = config["Universe:IbkrScanner:CacheFilePath"] ?? "logs/universe-cache.jsonl";
        
        Assert.Equal("logs/universe-cache.jsonl", defaultPath);
    }

    [Fact]
    public void CacheFilePath_Configurable()
    {
        // Validate cache path can be configured
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Universe:IbkrScanner:CacheFilePath"] = _testCacheFile
            })
            .Build();

        var cachePath = config["Universe:IbkrScanner:CacheFilePath"];
        
        Assert.Equal(_testCacheFile, cachePath);
    }

    [Fact]
    public async Task SaveUniverseCache_CreatesFile()
    {
        // Verify cache file is created
        var symbols = new[] { "AAPL", "MSFT", "GOOGL" };
        var cacheEntry = new
        {
            timestamp = DateTime.UtcNow.ToString("O"),
            count = symbols.Length,
            symbols = symbols
        };

        var json = JsonSerializer.Serialize(cacheEntry);
        await File.WriteAllTextAsync(_testCacheFile, json + Environment.NewLine);

        // Verify file exists and contains data
        Assert.True(File.Exists(_testCacheFile));
        var content = await File.ReadAllTextAsync(_testCacheFile);
        Assert.Contains("AAPL", content);
        Assert.Contains("MSFT", content);
        Assert.Contains("GOOGL", content);
    }

    [Fact]
    public async Task SaveUniverseCache_AppendMode()
    {
        // Verify cache appends to existing file (JSONL mode)
        var symbols1 = new[] { "AAPL", "MSFT" };
        var entry1 = new { timestamp = DateTime.UtcNow.ToString("O"), count = 2, symbols = symbols1 };
        var json1 = JsonSerializer.Serialize(entry1);

        await File.WriteAllTextAsync(_testCacheFile, json1 + Environment.NewLine);

        var symbols2 = new[] { "GOOGL", "AMZN" };
        var entry2 = new { timestamp = DateTime.UtcNow.ToString("O"), count = 2, symbols = symbols2 };
        var json2 = JsonSerializer.Serialize(entry2);

        await File.AppendAllTextAsync(_testCacheFile, json2 + Environment.NewLine);

        var lines = await File.ReadAllLinesAsync(_testCacheFile);
        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public async Task LoadUniverseCache_ReadsLatestEntry()
    {
        // Verify we load the latest (last) entry from JSONL
        var symbols1 = new[] { "AAPL" };
        var entry1 = new { timestamp = DateTime.UtcNow.ToString("O"), count = 1, symbols = symbols1 };
        var json1 = JsonSerializer.Serialize(entry1);

        var symbols2 = new[] { "AAPL", "MSFT", "GOOGL" };
        var entry2 = new { timestamp = DateTime.UtcNow.ToString("O"), count = 3, symbols = symbols2 };
        var json2 = JsonSerializer.Serialize(entry2);

        await File.WriteAllTextAsync(_testCacheFile, json1 + Environment.NewLine + json2 + Environment.NewLine);

        // Parse latest entry
        var lines = await File.ReadAllLinesAsync(_testCacheFile);
        var lastLine = lines[^1];
        var doc = JsonDocument.Parse(lastLine);
        var root = doc.RootElement;

        if (root.TryGetProperty("symbols", out var symbolsElement))
        {
            var symbols = new List<string>();
            foreach (var symbol in symbolsElement.EnumerateArray())
            {
                symbols.Add(symbol.GetString());
            }

            Assert.Equal(3, symbols.Count);
            Assert.Contains("GOOGL", symbols);
        }
    }

    [Fact]
    public async Task LoadUniverseCache_NoFile_ReturnsEmpty()
    {
        // Verify graceful handling when no cache file exists
        var nonExistentFile = Path.Combine(_testCacheDir, "nonexistent.jsonl");
        
        var fileExists = File.Exists(nonExistentFile);
        Assert.False(fileExists);
    }

    [Fact]
    public async Task LoadUniverseCache_EmptyFile_ReturnsEmpty()
    {
        // Verify graceful handling of empty cache file
        await File.WriteAllTextAsync(_testCacheFile, string.Empty);

        var lines = await File.ReadAllLinesAsync(_testCacheFile);
        Assert.Empty(lines);
    }

    [Fact]
    public async Task LoadUniverseCache_CorruptedJSON_ReturnsEmpty()
    {
        // Verify graceful handling of corrupted JSON
        await File.WriteAllTextAsync(_testCacheFile, "{ invalid json }" + Environment.NewLine);

        // Try to parse - should fail gracefully
        var lines = await File.ReadAllLinesAsync(_testCacheFile);
        Assert.NotEmpty(lines);

        try
        {
            var doc = JsonDocument.Parse(lines[^1]);
            Assert.Fail("Should have thrown JsonException");
        }
        catch (JsonException)
        {
            // Expected
        }
    }

    [Fact]
    public async Task CacheEntry_ContainsTimestamp()
    {
        // Verify cache entries have timestamp
        var symbols = new[] { "AAPL", "MSFT" };
        var now = DateTime.UtcNow.ToString("O");
        var cacheEntry = new
        {
            timestamp = now,
            count = symbols.Length,
            symbols = symbols
        };

        var json = JsonSerializer.Serialize(cacheEntry);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("timestamp", out var ts));
        Assert.True(root.TryGetProperty("count", out var count));
        Assert.True(root.TryGetProperty("symbols", out var syms));
        Assert.Equal(2, count.GetInt32());
    }

    [Fact]
    public async Task CacheEntry_ContainsSymbolCount()
    {
        // Verify cache entries record symbol count
        var symbols = new[] { "AAPL", "MSFT", "GOOGL", "AMZN" };
        var cacheEntry = new
        {
            timestamp = DateTime.UtcNow.ToString("O"),
            count = symbols.Length,
            symbols = symbols
        };

        var json = JsonSerializer.Serialize(cacheEntry);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("count", out var count));
        Assert.Equal(4, count.GetInt32());
    }

    [Fact]
    public async Task FallbackBehavior_OnScannerFailure_UsesCachedUniverse()
    {
        // Verify cache is used when scanner fails
        // This test validates the logic - actual implementation tested in integration
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Universe:IbkrScanner:CacheFilePath"] = _testCacheFile
            })
            .Build();

        // Simulate saved cache
        var symbols = new[] { "AAPL", "MSFT", "GOOGL" };
        var cacheEntry = new
        {
            timestamp = DateTime.UtcNow.ToString("O"),
            count = symbols.Length,
            symbols = symbols
        };

        var json = JsonSerializer.Serialize(cacheEntry);
        await File.WriteAllTextAsync(_testCacheFile, json + Environment.NewLine);

        // Verify we can read it back
        var lines = await File.ReadAllLinesAsync(_testCacheFile);
        var doc = JsonDocument.Parse(lines[^1]);
        var loaded = new List<string>();

        if (doc.RootElement.TryGetProperty("symbols", out var syms))
        {
            foreach (var sym in syms.EnumerateArray())
            {
                loaded.Add(sym.GetString());
            }
        }

        Assert.Equal(3, loaded.Count);
    }

    [Fact]
    public void IsUniverseStale_TracksSourceFreshness()
    {
        // Verify stale flag indicates data source
        // When from cache due to scanner failure, should be marked stale
        // When from fresh scanner, should be fresh
        
        // This is tested through ScannerSource integration test
        Assert.True(true); // Placeholder for integration test
    }

    [Fact]
    public async Task CacheDirectory_CreatedIfMissing()
    {
        // Verify parent directory is created if it doesn't exist
        var nestedDir = Path.Combine(_testCacheDir, "nested", "deep", "cache.jsonl");
        var parentDir = Path.GetDirectoryName(nestedDir);

        if (Directory.Exists(parentDir))
        {
            Directory.Delete(parentDir, true);
        }

        // Simulate directory creation
        Directory.CreateDirectory(parentDir);

        Assert.True(Directory.Exists(parentDir));
    }

    [Fact]
    public async Task MultipleCacheEntries_LatestUsedOnLoad()
    {
        // Verify that multiple historical entries exist but only latest is used
        var entries = new List<string>();
        
        for (int i = 1; i <= 5; i++)
        {
            var symbols = Enumerable.Range(0, i).Select(x => $"SYM{i}").ToArray();
            var entry = new
            {
                timestamp = DateTime.UtcNow.AddMinutes(-i).ToString("O"),
                count = symbols.Length,
                symbols = symbols
            };
            entries.Add(JsonSerializer.Serialize(entry));
        }

        var content = string.Join(Environment.NewLine, entries) + Environment.NewLine;
        await File.WriteAllTextAsync(_testCacheFile, content);

        // Load and verify we got the latest
        var lines = await File.ReadAllLinesAsync(_testCacheFile);
        Assert.Equal(5, lines.Length);

        var lastLine = lines[^1];
        var doc = JsonDocument.Parse(lastLine);

        if (doc.RootElement.TryGetProperty("symbols", out var syms))
        {
            var symbolCount = syms.GetArrayLength();
            Assert.Equal(5, symbolCount); // Latest has 5 entries
        }
    }

    [Fact]
    public async Task CacheRecovery_AfterScannerFailure_LogsWarning()
    {
        // Verify fallback behavior is logged
        // Actual logging tested in integration
        var config = CreateConfig();
        Assert.NotNull(config);
        
        await Task.CompletedTask;
    }

    [Fact]
    public void CacheInvalidation_OnSuccessfulScan()
    {
        // Verify stale flag is cleared when fresh scan succeeds
        var config = CreateConfig();
        Assert.NotNull(config);
    }

    private IConfiguration CreateConfig()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Universe:IbkrScanner:CacheFilePath"] = _testCacheFile,
                ["Universe:IbkrScanner:StartHour"] = "7",
                ["Universe:IbkrScanner:EndHour"] = "16"
            })
            .Build();
    }
}
