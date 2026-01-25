using RamStockAlerts.Services;
using Xunit;

namespace RamStockAlerts.Tests;

/// <summary>
/// Phase 2.3: Test journal rotation service for archiving trade journals.
/// </summary>
public class JournalRotationServiceTests : IDisposable
{
    private readonly string _tempDir;

    public JournalRotationServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"journal-rotation-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RotateJournal_JournalExists_CreatesDatedBackup()
    {
        // Arrange
        var service = new FileBasedJournalRotationService();
        var journalPath = Path.Combine(_tempDir, "trade-journal.jsonl");
        var testContent = "line1\nline2\nline3\n";
        await File.WriteAllTextAsync(journalPath, testContent);

        var rotationDate = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);

        // Act
        var result = await service.RotateJournalAsync(journalPath, rotationDate);

        // Assert
        Assert.True(result, "Rotation should succeed");
        Assert.False(File.Exists(journalPath), "Original journal should be moved");

        var rotatedPath = Path.Combine(_tempDir, "trade-journal-20260115.jsonl");
        Assert.True(File.Exists(rotatedPath), "Rotated journal should exist");

        var rotatedContent = await File.ReadAllTextAsync(rotatedPath);
        Assert.True(testContent == rotatedContent, "Content should be preserved");
    }

    [Fact]
    public async Task RotateJournal_JournalNotFound_ReturnsFalse()
    {
        // Arrange
        var service = new FileBasedJournalRotationService();
        var journalPath = Path.Combine(_tempDir, "nonexistent-journal.jsonl");
        var rotationDate = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);

        // Act
        var result = await service.RotateJournalAsync(journalPath, rotationDate);

        // Assert
        Assert.False(result, "Rotation should return false for nonexistent journal");
    }

    [Fact]
    public async Task RotateJournal_EmptyJournal_ReturnsFalse()
    {
        // Arrange
        var service = new FileBasedJournalRotationService();
        var journalPath = Path.Combine(_tempDir, "empty-journal.jsonl");
        await File.WriteAllTextAsync(journalPath, string.Empty);

        var rotationDate = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);

        // Act
        var result = await service.RotateJournalAsync(journalPath, rotationDate);

        // Assert
        Assert.False(result, "Rotation should return false for empty journal");
        Assert.True(File.Exists(journalPath), "Empty journal should remain");
    }

    [Fact]
    public async Task RotateJournal_TargetFileExists_AppendsAndDeletes()
    {
        // Arrange
        var service = new FileBasedJournalRotationService();
        var journalPath = Path.Combine(_tempDir, "trade-journal.jsonl");
        var rotatedPath = Path.Combine(_tempDir, "trade-journal-20260115.jsonl");

        var journalContent = "line1\nline2\n";
        var existingRotatedContent = "existing1\nexisting2\n";

        await File.WriteAllTextAsync(journalPath, journalContent);
        await File.WriteAllTextAsync(rotatedPath, existingRotatedContent);

        var rotationDate = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);

        // Act
        var result = await service.RotateJournalAsync(journalPath, rotationDate);

        // Assert
        Assert.True(result, "Rotation should succeed (append mode)");
        Assert.False(File.Exists(journalPath), "Original journal should be deleted after append");

        var finalContent = await File.ReadAllTextAsync(rotatedPath);
        Assert.True(finalContent.Contains(existingRotatedContent), "Final content should contain existing rotated content");
        Assert.True(finalContent.Contains(journalContent), "Final content should contain journal content");
    }

    [Fact]
    public async Task RotateJournal_WithoutExplicitDate_UsesCurrentDateUtc()
    {
        // Arrange
        var service = new FileBasedJournalRotationService();
        var journalPath = Path.Combine(_tempDir, "trade-journal.jsonl");
        var testContent = "test content\n";
        await File.WriteAllTextAsync(journalPath, testContent);

        var beforeRotation = DateTime.UtcNow;

        // Act
        var result = await service.RotateJournalAsync(journalPath);
        var afterRotation = DateTime.UtcNow;

        // Assert
        Assert.True(result, "Rotation should succeed");
        Assert.False(File.Exists(journalPath), "Original journal should be moved");

        // Check that rotated file exists with today's date
        var possibleDates = new[] 
        { 
            beforeRotation.AddDays(-1),
            beforeRotation,
            afterRotation
        };

        bool foundRotated = false;
        foreach (var date in possibleDates)
        {
            var rotatedPath = Path.Combine(_tempDir, $"trade-journal-{date:yyyyMMdd}.jsonl");
            if (File.Exists(rotatedPath))
            {
                foundRotated = true;
                var content = await File.ReadAllTextAsync(rotatedPath);
                Assert.Equal(testContent, content);
                break;
            }
        }

        Assert.True(foundRotated, "Rotated file with current date should exist");
    }

    [Fact]
    public async Task RotateJournal_LargeJournal_PreservesAllContent()
    {
        // Arrange
        var service = new FileBasedJournalRotationService();
        var journalPath = Path.Combine(_tempDir, "trade-journal.jsonl");

        // Create a large journal (100 lines)
        var lines = Enumerable.Range(1, 100).Select(i => $"{{\"line\":{i}}}");
        var testContent = string.Join("\n", lines) + "\n";
        await File.WriteAllTextAsync(journalPath, testContent);

        var rotationDate = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);

        // Act
        var result = await service.RotateJournalAsync(journalPath, rotationDate);

        // Assert
        Assert.True(result, "Rotation should succeed for large journal");

        var rotatedPath = Path.Combine(_tempDir, "trade-journal-20260115.jsonl");
        var rotatedContent = await File.ReadAllTextAsync(rotatedPath);
        Assert.True(testContent == rotatedContent, "All content should be preserved");
    }

    [Fact]
    public async Task RotateJournal_MultipleRotations_CreatesSeparateFiles()
    {
        // Arrange
        var service = new FileBasedJournalRotationService();
        var journalPath = Path.Combine(_tempDir, "trade-journal.jsonl");

        // First rotation (Day 1)
        await File.WriteAllTextAsync(journalPath, "day1-content\n");
        var date1 = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var result1 = await service.RotateJournalAsync(journalPath, date1);

        // Second rotation (Day 2)
        await File.WriteAllTextAsync(journalPath, "day2-content\n");
        var date2 = new DateTime(2026, 1, 16, 0, 0, 0, DateTimeKind.Utc);
        var result2 = await service.RotateJournalAsync(journalPath, date2);

        // Assert
        Assert.True(result1, "First rotation should succeed");
        Assert.True(result2, "Second rotation should succeed");

        var rotated1 = Path.Combine(_tempDir, "trade-journal-20260115.jsonl");
        var rotated2 = Path.Combine(_tempDir, "trade-journal-20260116.jsonl");

        Assert.True(File.Exists(rotated1), "First rotated file should exist");
        Assert.True(File.Exists(rotated2), "Second rotated file should exist");

        var content1 = await File.ReadAllTextAsync(rotated1);
        var content2 = await File.ReadAllTextAsync(rotated2);

        Assert.True(content1.Contains("day1-content"), "First rotated file should contain day1 content");
        Assert.True(content2.Contains("day2-content"), "Second rotated file should contain day2 content");
    }

    [Fact]
    public async Task RotateJournal_CancellationRequested_Throws()
    {
        // Arrange
        var service = new FileBasedJournalRotationService();
        var journalPath = Path.Combine(_tempDir, "trade-journal.jsonl");
        await File.WriteAllTextAsync(journalPath, "test\n");

        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.RotateJournalAsync(journalPath, cts.Token));
    }

    [Fact]
    public async Task RotateJournal_NullPath_ReturnsFalse()
    {
        // Arrange
        var service = new FileBasedJournalRotationService();

        // Act
        var result = await service.RotateJournalAsync(null!);

        // Assert
        Assert.False(result, "Rotation should return false for null path");
    }

    [Fact]
    public async Task RotateJournal_EmptyPath_ReturnsFalse()
    {
        // Arrange
        var service = new FileBasedJournalRotationService();

        // Act
        var result = await service.RotateJournalAsync(string.Empty);

        // Assert
        Assert.False(result, "Rotation should return false for empty path");
    }
}

