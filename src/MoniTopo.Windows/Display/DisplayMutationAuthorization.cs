using System.Diagnostics.CodeAnalysis;

namespace MoniTopo.Windows.Display;

public sealed class DisplayMutationAuthorization
{
    private const string OptInVariable = "MONITOPO_ALLOW_REAL_DISPLAY_CHANGES";

    private DisplayMutationAuthorization()
    {
    }

    public static bool TryCreateForExplicitManualCommand(
        bool explicitManualCommand,
        [NotNullWhen(true)] out DisplayMutationAuthorization? authorization) =>
        TryCreate(explicitManualCommand, Environment.GetEnvironmentVariable, out authorization);

    internal static bool TryCreate(
        bool explicitManualCommand,
        Func<string, string?> environmentReader,
        [NotNullWhen(true)] out DisplayMutationAuthorization? authorization)
    {
        if (explicitManualCommand && string.Equals(environmentReader(OptInVariable), "1", StringComparison.Ordinal))
        {
            authorization = new DisplayMutationAuthorization();
            return true;
        }

        authorization = null;
        return false;
    }
}
