using Serilog;

namespace RamStockAlerts.Services;

/// <summary>
/// File-based implementation of journal rotation.
/// Rotates trade-journal.jsonl → trade-journal-yyyyMMdd.jsonl
/// </summary>
public sealed class FileBasedJournalRotationService : IJournalRotationService
{
    private readonly global::Serilog.ILogger _logger = Log.ForContext<FileBasedJournalRotationService>();

    public async Task<bool> RotateJournalAsync(string journalPath, CancellationToken cancellationToken = default)
    {
        return await RotateJournalAsync(journalPath, DateTime.UtcNow, cancellationToken);
    }

    public async Task<bool> RotateJournalAsync(string journalPath, DateTime rotationDate, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(journalPath))
        {
            _logger.Warning("Journal rotation: empty path provided");
            return false;
        }

        if (!File.Exists(journalPath))
        {
            _logger.Information("Journal rotation: journal file not found at {Path}, skipping", journalPath);
            return false;
        }

        // Check if journal is empty (0 bytes)
        var fileInfo = new FileInfo(journalPath);
        if (fileInfo.Length == 0)
        {
            _logger.Information("Journal rotation: journal is empty (0 bytes), skipping");
            return false;
        }

        var dir = Path.GetDirectoryName(journalPath) ?? ".";
        var baseName = Path.GetFileNameWithoutExtension(journalPath);
        var extension = Path.GetExtension(journalPath); // e.g., ".jsonl"
        var rotatedFileName = $"{baseName}-{rotationDate:yyyyMMdd}{extension}";
        var rotatedPath = Path.Combine(dir, rotatedFileName);

        try
        {
            // Rotate: move current journal to dated backup
            File.Move(journalPath, rotatedPath, overwrite: false);
            
            _logger.Information("Journal rotation completed: {SourcePath} → {DestPath}", journalPath, rotatedPath);
            return true;
        }
        catch (IOException) when (File.Exists(rotatedPath))
        {
            // Target file already exists (e.g., already rotated today)
            _logger.Information("Journal rotation: target file {Path} already exists, appending instead", rotatedPath);
            
            try
            {
                // Append current journal to existing rotated file and delete current
                using (var sourceStream = File.OpenRead(journalPath))
                using (var destStream = File.Open(rotatedPath, FileMode.Append, FileAccess.Write))
                {
                    await sourceStream.CopyToAsync(destStream, cancellationToken);
                }

                File.Delete(journalPath);
                _logger.Information("Journal rotation completed (append mode): {SourcePath} → {DestPath}", journalPath, rotatedPath);
                return true;
            }
            catch (Exception appendEx)
            {
                _logger.Error(appendEx, "Failed to append journal {SourcePath} to {DestPath}", journalPath, rotatedPath);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to rotate journal {SourcePath} to {DestPath}", journalPath, rotatedPath);
            return false;
        }
    }
}
