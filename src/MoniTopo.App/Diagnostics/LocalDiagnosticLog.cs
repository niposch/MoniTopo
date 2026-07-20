using System.Globalization;
using System.IO;
using System.Security;
using System.Text;

namespace MoniTopo.App.Diagnostics;

public sealed class LocalDiagnosticLog
{
    private readonly object _sync = new();
    private readonly string _directory;
    private readonly string _logPath;
    private readonly int _maximumFiles;
    private readonly long _maximumBytes;

    public LocalDiagnosticLog(
        string directory,
        TimeProvider? timeProvider = null,
        int maximumFiles = 7,
        long maximumBytes = 1024 * 1024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFiles, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1024);
        _directory = Path.GetFullPath(directory);
        _maximumFiles = maximumFiles;
        _maximumBytes = maximumBytes;
        var timestamp = (timeProvider ?? TimeProvider.System).GetUtcNow()
            .ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
        _logPath = Path.Combine(_directory, $"monitopo-{timestamp}-{Environment.ProcessId}.log");
    }

    public void Write(string area, Exception exception) => Write(area, exception.Message, exception);

    public void Write(string area, string message, Exception? exception = null)
    {
        if (string.IsNullOrWhiteSpace(area) || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        try
        {
            lock (_sync)
            {
                Directory.CreateDirectory(_directory);
                var detail = exception is null ? string.Empty : $"{Environment.NewLine}{exception}";
                var entry = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{DateTimeOffset.UtcNow:O} [{area.Trim()}] {message.Trim()}{detail}{Environment.NewLine}");
                var byteCount = Encoding.UTF8.GetByteCount(entry);
                var currentLength = File.Exists(_logPath) ? new FileInfo(_logPath).Length : 0;
                if (currentLength + byteCount <= _maximumBytes)
                {
                    File.AppendAllText(_logPath, entry, Encoding.UTF8);
                }

                RemoveExpiredFiles();
            }
        }
        catch (Exception loggingException) when (loggingException is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Diagnostics must never become an application failure.
        }
    }

    private void RemoveExpiredFiles()
    {
        var files = new DirectoryInfo(_directory)
            .GetFiles("monitopo-*.log")
            .OrderByDescending(file => file.Name, StringComparer.Ordinal)
            .Skip(_maximumFiles)
            .ToArray();
        foreach (var file in files)
        {
            file.Delete();
        }
    }
}
