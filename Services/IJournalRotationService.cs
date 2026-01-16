namespace RamStockAlerts.Services;

/// <summary>
/// Service for rotating (archiving) the shadow trade journal to dated backups.
/// Prevents single journal file from growing unbounded.
/// </summary>
public interface IJournalRotationService
{
    /// <summary>
    /// Rotate the current journal file to a dated backup if it exists and is not empty.
    /// Source: shadow-trade-journal.jsonl → shadow-trade-journal-{yyyyMMdd}.jsonl
    /// </summary>
    /// <param name="journalPath">Path to the current journal file (e.g., logs/shadow-trade-journal.jsonl)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if rotation occurred, false if journal doesn't exist or is empty</returns>
    Task<bool> RotateJournalAsync(string journalPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rotate journal with explicit target date (for testing and historical rotation).
    /// </summary>
    /// <param name="journalPath">Path to the current journal file</param>
    /// <param name="rotationDate">The date to use for the rotated filename</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if rotation occurred, false if journal doesn't exist or is empty</returns>
    Task<bool> RotateJournalAsync(string journalPath, DateTime rotationDate, CancellationToken cancellationToken = default);
}
