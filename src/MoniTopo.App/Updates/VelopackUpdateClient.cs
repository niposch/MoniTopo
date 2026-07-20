using System.Net;
using System.Net.Http;
using MoniTopo.Core.Updates;
using MoniTopo.Core.Versioning;
using Velopack;
using Velopack.Exceptions;
using Velopack.Sources;

namespace MoniTopo.App.Updates;

public sealed class VelopackUpdateClient : IUpdateClient
{
    private const string RepositoryUrl = "https://github.com/niposch/MoniTopo";
    private readonly UpdateManager _manager;
    private UpdateInfo? _available;

    public VelopackUpdateClient()
        : this(new UpdateManager(new GithubSource(RepositoryUrl, accessToken: null, prerelease: true)))
    {
    }

    internal VelopackUpdateClient(UpdateManager manager) => _manager = manager;

    public bool IsInstalled => _manager.IsInstalled;

    public string? CurrentPackageVersion => _manager.CurrentVersion?.ToString();

    public AvailableUpdate? PendingRestart => _manager.UpdatePendingRestart is { } pending
        ? ToAvailableUpdate(pending)
        : null;

    public async Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _available = await _manager.CheckForUpdatesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            return _available is null ? null : ToAvailableUpdate(_available.TargetFullRelease);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Translate(exception);
        }
    }

    public async Task DownloadAsync(
        AvailableUpdate update,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_available is null ||
            !string.Equals(_available.TargetFullRelease.Version.ToString(), update.PackageVersion, StringComparison.Ordinal))
        {
            throw new UpdateClientException(UpdateClientError.Unknown, "The selected update is no longer available.");
        }

        try
        {
            await _manager.DownloadUpdatesAsync(
                _available,
                value => progress?.Report(value),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Translate(exception);
        }
    }

    public void ApplyAndRestart(AvailableUpdate update)
    {
        var asset = _manager.UpdatePendingRestart ?? _available?.TargetFullRelease;
        if (asset is null || !string.Equals(asset.Version.ToString(), update.PackageVersion, StringComparison.Ordinal))
        {
            throw new UpdateClientException(UpdateClientError.Unknown, "The downloaded update is no longer available.");
        }

        try
        {
            _manager.ApplyUpdatesAndRestart(asset);
        }
        catch (Exception exception)
        {
            throw Translate(exception);
        }
    }

    private static AvailableUpdate ToAvailableUpdate(VelopackAsset asset)
    {
        var packageVersion = asset.Version.ToString();
        var displayVersion = ReleaseVersion.TryParse(packageVersion, out var version)
            ? version.DisplayVersion
            : packageVersion;
        return new AvailableUpdate(packageVersion, displayVersion, asset.NotesMarkdown);
    }

    private static UpdateClientException Translate(Exception exception) => exception switch
    {
        NotInstalledException => new UpdateClientException(UpdateClientError.NotInstalled, exception.Message, exception),
        ChecksumFailedException => new UpdateClientException(UpdateClientError.Checksum, exception.Message, exception),
        AcquireLockFailedException => new UpdateClientException(UpdateClientError.Busy, exception.Message, exception),
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests } =>
            new UpdateClientException(UpdateClientError.RateLimited, exception.Message, exception),
        HttpRequestException => new UpdateClientException(UpdateClientError.Network, exception.Message, exception),
        _ => new UpdateClientException(UpdateClientError.Unknown, exception.Message, exception),
    };
}
