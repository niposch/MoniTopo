using System.Diagnostics.CodeAnalysis;
using MoniTopo.Core.Models;

namespace MoniTopo.Core.Identity;

public enum IdentityResolutionStatus
{
    Success,
    Missing,
    Ambiguous,
}

public sealed record IdentityScore(int Score, IReadOnlyList<string> MatchedSignals)
{
    public bool MeetsThreshold => Score >= MonitorIdentityResolver.MinimumScore;
}

public sealed record ResolvedIdentityBinding(string DisplayId, string RuntimeId, int Score);

public sealed record IdentityResolutionResult(
    IdentityResolutionStatus Status,
    IReadOnlyList<ResolvedIdentityBinding> Bindings,
    string? ProblemDisplayLabel,
    string? Message)
{
    public static IdentityResolutionResult Missing(string label) => new(
        IdentityResolutionStatus.Missing,
        Array.Empty<ResolvedIdentityBinding>(),
        label,
        $"The required display \"{label}\" is not connected.");

    public static IdentityResolutionResult Ambiguous(string label) => new(
        IdentityResolutionStatus.Ambiguous,
        Array.Empty<ResolvedIdentityBinding>(),
        label,
        $"Two connected displays could match \"{label}\". Open Settings to resolve the mapping.");
}

public sealed class MonitorIdentityResolver
{
    public const int MinimumScore = 75;
    private const int AmbiguityMargin = 10;
    private const int InvalidScore = -1_000_000;

    public static IdentityScore Score(MonitorIdentityFingerprint saved, MonitorIdentityFingerprint candidate, bool rememberedBinding = false)
    {
        var score = 0;
        var signals = new List<string>();
        if (Conflicts(saved.DeviceContainerId, candidate.DeviceContainerId))
        {
            score -= 200;
        }

        if (Conflicts(saved.EdidSerial, candidate.EdidSerial))
        {
            score -= 200;
        }

        AddMatch(saved.MonitorDevicePath, candidate.MonitorDevicePath, 110, "monitor device path", ref score, signals);
        AddMatch(saved.DeviceInstanceId, candidate.DeviceInstanceId, 100, "device instance", ref score, signals);
        AddMatch(saved.DeviceContainerId, candidate.DeviceContainerId, 105, "device container", ref score, signals);
        AddMatch(saved.EdidSerial, candidate.EdidSerial, 95, "EDID serial", ref score, signals);

        if (Equal(saved.EdidManufacturerId, candidate.EdidManufacturerId) && saved.EdidProductCode == candidate.EdidProductCode)
        {
            score += 35;
            signals.Add("EDID manufacturer/product");
        }

        AddMatch(saved.FriendlyModelName, candidate.FriendlyModelName, 15, "model name", ref score, signals);
        if (saved.PhysicalWidthMillimeters is int savedWidth &&
            saved.PhysicalHeightMillimeters is int savedHeight &&
            candidate.PhysicalWidthMillimeters == savedWidth &&
            candidate.PhysicalHeightMillimeters == savedHeight)
        {
            score += 10;
            signals.Add("physical dimensions");
        }

        if (saved.OutputTechnology != DisplayOutputTechnology.Unknown && saved.OutputTechnology == candidate.OutputTechnology)
        {
            score += 10;
            signals.Add("output technology");
            if (saved.ConnectorInstance is uint connector && candidate.ConnectorInstance == connector)
            {
                score += 20;
                signals.Add("connector instance");
            }
        }

        if (saved.PreferredMode is DisplaySize preferred && candidate.PreferredMode == preferred)
        {
            score += 8;
            signals.Add("preferred mode");
        }

        AddMatch(saved.SupportedModeSignature, candidate.SupportedModeSignature, 8, "mode signature", ref score, signals);
        if (rememberedBinding)
        {
            score += 200;
            signals.Add("remembered binding");
        }

        return new IdentityScore(score, signals);
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "The resolver is an injectable domain service.")]
    public IdentityResolutionResult Resolve(DisplayProfile profile, IReadOnlyList<ConnectedDisplayState> candidates)
    {
        if (profile.Displays.Count > candidates.Count)
        {
            return IdentityResolutionResult.Missing(FirstUnresolvableLabel(profile, candidates));
        }

        var orderedCandidates = candidates.OrderBy(candidate => candidate.RuntimeId, StringComparer.Ordinal).ToArray();
        var weights = new int[profile.Displays.Count, orderedCandidates.Length];
        for (var row = 0; row < profile.Displays.Count; row++)
        {
            var savedDisplay = profile.Displays[row];
            var rememberedRuntimeId = profile.LastSuccessfulIdentityBindings
                .FirstOrDefault(binding => string.Equals(binding.DisplayId, savedDisplay.DisplayId, StringComparison.OrdinalIgnoreCase))
                ?.RuntimeIdentityKey;
            for (var column = 0; column < orderedCandidates.Length; column++)
            {
                var candidate = orderedCandidates[column];
                var remembered = string.Equals(rememberedRuntimeId, candidate.RuntimeId, StringComparison.Ordinal);
                var candidateScore = Score(savedDisplay.Identity, candidate.Identity, remembered).Score;
                weights[row, column] = candidateScore >= MinimumScore ? candidateScore : InvalidScore;
            }

            if (Enumerable.Range(0, orderedCandidates.Length).All(column => weights[row, column] == InvalidScore))
            {
                return IdentityResolutionResult.Missing(savedDisplay.FriendlyLabel);
            }
        }

        var best = FindMaximumAssignment(weights, forbiddenRow: null, forbiddenColumn: null);
        if (best is null)
        {
            return IdentityResolutionResult.Missing(FirstUnresolvableLabel(profile, candidates));
        }

        for (var row = 0; row < best.Columns.Length; row++)
        {
            var alternative = FindMaximumAssignment(weights, row, best.Columns[row]);
            if (alternative is not null && alternative.TotalScore >= best.TotalScore - AmbiguityMargin)
            {
                return IdentityResolutionResult.Ambiguous(profile.Displays[row].FriendlyLabel);
            }
        }

        var bindings = best.Columns
            .Select((column, row) => new ResolvedIdentityBinding(
                profile.Displays[row].DisplayId,
                orderedCandidates[column].RuntimeId,
                weights[row, column]))
            .ToArray();
        return new IdentityResolutionResult(IdentityResolutionStatus.Success, bindings, null, null);
    }

    private static string FirstUnresolvableLabel(DisplayProfile profile, IReadOnlyList<ConnectedDisplayState> candidates) =>
        profile.Displays.FirstOrDefault(display => candidates.All(candidate => !Score(display.Identity, candidate.Identity).MeetsThreshold))
            ?.FriendlyLabel ?? profile.Displays[0].FriendlyLabel;

    private static Assignment? FindMaximumAssignment(int[,] weights, int? forbiddenRow, int? forbiddenColumn)
    {
        var rowCount = weights.GetLength(0);
        var columnCount = weights.GetLength(1);
        if (rowCount == 0 || rowCount > columnCount)
        {
            return null;
        }

        var maximumWeight = 0;
        for (var row = 0; row < rowCount; row++)
        {
            for (var column = 0; column < columnCount; column++)
            {
                if (weights[row, column] != InvalidScore)
                {
                    maximumWeight = Math.Max(maximumWeight, weights[row, column]);
                }
            }
        }

        const long invalidCost = 1_000_000_000;
        var rowPotential = new long[rowCount + 1];
        var columnPotential = new long[columnCount + 1];
        var matchedRow = new int[columnCount + 1];
        var previousColumn = new int[columnCount + 1];
        for (var currentRow = 1; currentRow <= rowCount; currentRow++)
        {
            matchedRow[0] = currentRow;
            var minimumCost = Enumerable.Repeat(long.MaxValue, columnCount + 1).ToArray();
            var used = new bool[columnCount + 1];
            var currentColumn = 0;
            do
            {
                used[currentColumn] = true;
                var activeRow = matchedRow[currentColumn];
                var delta = long.MaxValue;
                var nextColumn = 0;
                for (var column = 1; column <= columnCount; column++)
                {
                    if (used[column])
                    {
                        continue;
                    }

                    var rowIndex = activeRow - 1;
                    var columnIndex = column - 1;
                    var forbidden = forbiddenRow == rowIndex && forbiddenColumn == columnIndex;
                    var weight = forbidden ? InvalidScore : weights[rowIndex, columnIndex];
                    var baseCost = weight == InvalidScore ? invalidCost : maximumWeight - weight;
                    var reducedCost = baseCost - rowPotential[activeRow] - columnPotential[column];
                    if (reducedCost < minimumCost[column])
                    {
                        minimumCost[column] = reducedCost;
                        previousColumn[column] = currentColumn;
                    }

                    if (minimumCost[column] < delta)
                    {
                        delta = minimumCost[column];
                        nextColumn = column;
                    }
                }

                if (delta == long.MaxValue)
                {
                    return null;
                }

                for (var column = 0; column <= columnCount; column++)
                {
                    if (used[column])
                    {
                        rowPotential[matchedRow[column]] += delta;
                        columnPotential[column] -= delta;
                    }
                    else
                    {
                        minimumCost[column] -= delta;
                    }
                }

                currentColumn = nextColumn;
            }
            while (matchedRow[currentColumn] != 0);

            do
            {
                var previous = previousColumn[currentColumn];
                matchedRow[currentColumn] = matchedRow[previous];
                currentColumn = previous;
            }
            while (currentColumn != 0);
        }

        var columns = Enumerable.Repeat(-1, rowCount).ToArray();
        for (var column = 1; column <= columnCount; column++)
        {
            if (matchedRow[column] > 0)
            {
                columns[matchedRow[column] - 1] = column - 1;
            }
        }

        if (columns.Any(column => column < 0) ||
            columns.Select((column, row) =>
                forbiddenRow == row && forbiddenColumn == column ? InvalidScore : weights[row, column])
                .Any(weight => weight == InvalidScore))
        {
            return null;
        }

        var totalScore = columns.Select((column, row) => weights[row, column]).Sum();
        return new Assignment(columns, totalScore);
    }

    private static void AddMatch(
        string? saved,
        string? candidate,
        int points,
        string signal,
        ref int score,
        List<string> signals)
    {
        if (Equal(saved, candidate))
        {
            score += points;
            signals.Add(signal);
        }
    }

    private static bool Equal(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool Conflicts(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        !string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private sealed record Assignment(int[] Columns, int TotalScore);
}
