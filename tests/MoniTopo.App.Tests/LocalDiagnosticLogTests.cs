using MoniTopo.App.Diagnostics;

namespace MoniTopo.App.Tests;

public sealed class LocalDiagnosticLogTests
{
    [Fact]
    public async Task WritesTechnicalDetailAndKeepsBoundedFileCount()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MoniTopo.App.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            for (var index = 0; index < 3; index++)
            {
                var time = new DateTimeOffset(2026, 7, 20, 12, 0, index, TimeSpan.Zero);
                var log = new LocalDiagnosticLog(directory, new FrozenTimeProvider(time), maximumFiles: 2);
                log.Write("Update", new InvalidOperationException($"failure {index}"));
                await Task.Delay(2);
            }

            var files = Directory.GetFiles(directory, "monitopo-*.log");
            Assert.Equal(2, files.Length);
            var text = string.Join(Environment.NewLine, files.Select(File.ReadAllText));
            Assert.Contains("[Update]", text, StringComparison.Ordinal);
            Assert.Contains("InvalidOperationException", text, StringComparison.Ordinal);
            Assert.DoesNotContain("failure 0", text, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class FrozenTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
