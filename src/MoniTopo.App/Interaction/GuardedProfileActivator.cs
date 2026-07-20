using MoniTopo.App.State;
using MoniTopo.Core.Activation;
using MoniTopo.Core.Identity;
using MoniTopo.Core.Matching;
using MoniTopo.Core.Models;
using MoniTopo.Windows.Display;

namespace MoniTopo.App.Interaction;

public sealed class GuardedProfileActivator(ConfigurationSession configuration) : IProfileActivator, IDisposable
{
    private readonly object _sync = new();
    private ProfileActivationService? _service;

    public Task<ActivationResult> ActivateAsync(
        DisplayProfile profile,
        IProgress<ActivationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var service = GetOrCreateService();
        return service is null
            ? Task.FromResult(new ActivationResult(
                ActivationOutcome.Failed,
                "Display activation is disabled. Use an explicit manual activation command with MONITOPO_ALLOW_REAL_DISPLAY_CHANGES=1.",
                "activation.safety-opt-in-required",
                false,
                false,
                false,
                false))
            : service.ActivateAsync(profile, progress, cancellationToken);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _service?.Dispose();
            _service = null;
        }
    }

    private ProfileActivationService? GetOrCreateService()
    {
        lock (_sync)
        {
            if (_service is not null)
            {
                return _service;
            }

            if (!DisplayMutationAuthorization.TryCreateForExplicitManualCommand(
                    explicitManualCommand: true,
                    out var authorization))
            {
                return null;
            }

            var resolver = new MonitorIdentityResolver();
            _service = new ProfileActivationService(
                new GuardedDisplayActivationBackend(authorization),
                new ProcessRecoveryCoordinator(),
                configuration,
                resolver,
                new ActiveProfileMatcher(resolver));
            return _service;
        }
    }
}
