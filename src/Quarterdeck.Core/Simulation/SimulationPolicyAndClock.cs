using System.Security.Cryptography;
using System.Text;

namespace Quarterdeck.Core.Simulation;

public enum SimulationPolicyOptionClassification
{
    Required = 0,
    OptionalSelected = 1,
    NotSelected = 2
}

public readonly record struct SimulationPolicySelection(
    string OptionCode,
    SimulationPolicyOptionClassification Classification);

public sealed class SimulationPolicySnapshot
{
    private SimulationPolicySnapshot(
        string authorityId,
        string version,
        IReadOnlyList<SimulationPolicySelection> selections,
        string contentDigest)
    {
        AuthorityId = authorityId;
        Version = version;
        Selections = selections;
        SelectedOptions = Array.AsReadOnly(selections
            .Where(selection => selection.Classification != SimulationPolicyOptionClassification.NotSelected)
            .Select(selection => selection.OptionCode)
            .ToArray());
        ContentDigest = contentDigest;
    }

    public string AuthorityId { get; }
    public string Version { get; }
    public IReadOnlyList<SimulationPolicySelection> Selections { get; }
    public IReadOnlyList<string> SelectedOptions { get; }
    public string ContentDigest { get; }
    public string Identity => $"{AuthorityId}:{Version}:{ContentDigest}";

    public static SimulationPolicySnapshot Create(
        string authorityId,
        string version,
        IEnumerable<string>? selectedOptions = null)
    {
        var normalizedAuthorityId = RequireToken(authorityId, nameof(authorityId));
        var normalizedVersion = RequireToken(version, nameof(version));
        var selections = (selectedOptions ?? [])
            .Select(option => new SimulationPolicySelection(
                RequireToken(option, nameof(selectedOptions)),
                SimulationPolicyOptionClassification.Required));

        return CreateClassified(normalizedAuthorityId, normalizedVersion, selections);
    }

    public static SimulationPolicySnapshot CreateClassified(
        string authorityId,
        string version,
        IEnumerable<SimulationPolicySelection>? selections = null)
    {
        var normalizedAuthorityId = RequireToken(authorityId, nameof(authorityId));
        var normalizedVersion = RequireToken(version, nameof(version));
        var normalizedSelections = (selections ?? [])
            .Select(selection => new SimulationPolicySelection(
                RequireToken(selection.OptionCode, nameof(selections)),
                RequireClassification(selection.Classification)))
            .GroupBy(selection => selection.OptionCode, StringComparer.Ordinal)
            .Select(group => group.Select(selection => selection.Classification).Distinct().Count() == 1
                ? group.First()
                : throw new ArgumentException("policy_option_classification_conflict", nameof(selections)))
            .OrderBy(selection => selection.OptionCode, StringComparer.Ordinal)
            .ToArray();

        return new SimulationPolicySnapshot(
            normalizedAuthorityId,
            normalizedVersion,
            Array.AsReadOnly(normalizedSelections),
            ComputeContentDigest(normalizedVersion, normalizedSelections));
    }

    internal bool HasSameContent(SimulationPolicySnapshot other) =>
        AuthorityId == other.AuthorityId
        && Version == other.Version
        && ContentDigest == other.ContentDigest
        && Selections.SequenceEqual(other.Selections);

    private static string ComputeContentDigest(
        string version,
        IReadOnlyList<SimulationPolicySelection> selections)
    {
        var canonical = new StringBuilder("quarterdeck-simulation-policy-v2\n")
            .Append("version:")
            .Append(version)
            .Append('\n');
        foreach (var selection in selections)
        {
            canonical.Append("option:")
                .Append(selection.OptionCode)
                .Append(':')
                .Append(selection.Classification)
                .Append('\n');
        }

        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static SimulationPolicyOptionClassification RequireClassification(
        SimulationPolicyOptionClassification classification)
    {
        if (!Enum.IsDefined(classification))
        {
            throw new ArgumentException("policy_option_classification_invalid", nameof(classification));
        }

        return classification;
    }

    private static string RequireToken(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 80
            || value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new ArgumentException(
                "Simulation policy values must be bounded ASCII tokens.",
                parameterName);
        }

        return value;
    }
}

public interface ISimulationPolicySource
{
    SimulationPolicySnapshot CaptureCurrent();
}

/// <summary>
/// Mutable only to drive deterministic server-free simulations. Lifecycles bind
/// immutable snapshots and never retain this source's caller-owned collections.
/// </summary>
public sealed class SyntheticSimulationPolicySource : ISimulationPolicySource
{
    private readonly object _sync = new();
    private readonly Dictionary<string, SimulationPolicySnapshot> _snapshotsByVersion =
        new(StringComparer.Ordinal);
    private readonly string _authorityId;
    private SimulationPolicySnapshot _current;

    public SyntheticSimulationPolicySource(SimulationPolicySnapshot initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _authorityId = initial.AuthorityId;
        _current = SimulationPolicySnapshot.CreateClassified(
            initial.AuthorityId,
            initial.Version,
            initial.Selections);
        _snapshotsByVersion.Add(_current.Version, _current);
    }

    public SimulationPolicySnapshot CaptureCurrent()
    {
        lock (_sync)
        {
            return SimulationPolicySnapshot.CreateClassified(
                _current.AuthorityId,
                _current.Version,
                _current.Selections);
        }
    }

    public void Replace(SimulationPolicySnapshot replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        lock (_sync)
        {
            var stable = SimulationPolicySnapshot.CreateClassified(
                replacement.AuthorityId,
                replacement.Version,
                replacement.Selections);
            if (!string.Equals(stable.AuthorityId, _authorityId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("policy_authority_mismatch");
            }

            if (_snapshotsByVersion.TryGetValue(stable.Version, out var existing))
            {
                if (!existing.HasSameContent(stable))
                {
                    throw new InvalidOperationException("policy_version_mismatch");
                }

                _current = existing;
                return;
            }

            _snapshotsByVersion.Add(stable.Version, stable);
            _current = stable;
        }
    }
}

public readonly record struct SimulationClockObservation(
    bool Available,
    DateTimeOffset UtcNow)
{
    public static SimulationClockObservation Unavailable => new(false, default);

    public static SimulationClockObservation At(DateTimeOffset utcNow)
    {
        if (utcNow.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("simulation_clock_not_utc", nameof(utcNow));
        }

        return new SimulationClockObservation(true, utcNow);
    }
}

public interface ISimulationApprovalClock
{
    SimulationClockObservation Observe();
}

/// <summary>
/// Explicit test clock. It never consults the host wall clock and may be moved
/// backward so fail-closed behavior can be exercised without sleeping.
/// </summary>
public sealed class SyntheticSimulationApprovalClock : ISimulationApprovalClock
{
    private readonly object _sync = new();
    private SimulationClockObservation _current;
    private bool _acceptanceCommitActive;

    public SyntheticSimulationApprovalClock(DateTimeOffset initialUtc)
    {
        _current = SimulationClockObservation.At(initialUtc);
    }

    public SimulationClockObservation Observe()
    {
        lock (_sync)
        {
            return _current;
        }
    }

    public void SetUtc(DateTimeOffset utcNow)
    {
        lock (_sync)
        {
            RequireMutationAllowed();
            _current = SimulationClockObservation.At(utcNow);
        }
    }

    public void SetUnavailable()
    {
        lock (_sync)
        {
            RequireMutationAllowed();
            _current = SimulationClockObservation.Unavailable;
        }
    }

    internal void CommitSynchronizedAcceptance(
        SyntheticIdentityDirectory directory,
        SyntheticDispatchAdmission admission,
        ISyntheticDispatchAcceptanceCommitter committer)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(committer);
        lock (_sync)
        {
            RequireMutationAllowed();
            _acceptanceCommitActive = true;
            try
            {
                directory.CommitSynchronizedAcceptance(admission, _current, committer);
            }
            finally
            {
                _acceptanceCommitActive = false;
            }
        }
    }

    private void RequireMutationAllowed()
    {
        if (_acceptanceCommitActive)
            throw new InvalidOperationException("simulation_acceptance_reentry");
    }
}
