using System.IO;
using MoniTopo.App.Updates;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;

namespace MoniTopo.App.Tests;

public sealed class UpdateVersionCompatibilityTests
{
    [Theory]
    [InlineData("2026.1022.0", null)]
    [InlineData("2026.721.0", null)]
    [InlineData("2026.1022.1", "2026.10.22.1")]
    [InlineData("2026.1023.0", "2026.10.23")]
    [InlineData("2027.101.0", "2027.01.01")]
    public async Task VelopackComparesPackageVersionsButAppDisplaysCalendarLabels(
        string offeredVersion, string? expectedDisplay)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MoniTopo-update-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = new MemorySource(offeredVersion);
            var locator = new TestVelopackLocator("MoniTopo", "2026.1022.0", directory);
            var client = new VelopackUpdateClient(new UpdateManager(source, new UpdateOptions(), locator));

            Assert.True(client.IsInstalled);
            Assert.Equal("2026.1022.0", client.CurrentPackageVersion);
            var update = await client.CheckAsync();

            if (expectedDisplay is null)
            {
                Assert.Null(update);
            }
            else
            {
                Assert.NotNull(update);
                Assert.Equal(offeredVersion, update.PackageVersion);
                Assert.Equal(expectedDisplay, update.DisplayVersion);
                Assert.Equal("Synthetic release notes", update.ReleaseNotes);
            }
            Assert.Equal(0, source.DownloadCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class MemorySource(string version) : IUpdateSource
    {
        public int DownloadCount { get; private set; }

        public Task<VelopackAssetFeed> GetReleaseFeed(
            IVelopackLogger logger, string? appId, string channel, Guid? stagingId, VelopackAsset? latestLocalRelease) =>
            Task.FromResult(new VelopackAssetFeed
            {
                Assets =
                [
                    new VelopackAsset
                    {
                        PackageId = "MoniTopo",
                        Version = SemanticVersion.Parse(version),
                        Type = VelopackAssetType.Full,
                        FileName = $"MoniTopo-{version}-full.nupkg",
                        NotesMarkdown = "Synthetic release notes",
                    },
                ],
            });

        public Task DownloadReleaseEntry(
            IVelopackLogger logger, VelopackAsset releaseEntry, string localFile,
            Action<int> progress, CancellationToken cancelToken)
        {
            DownloadCount++;
            throw new InvalidOperationException("Version checks must not download or install an update.");
        }
    }
}
