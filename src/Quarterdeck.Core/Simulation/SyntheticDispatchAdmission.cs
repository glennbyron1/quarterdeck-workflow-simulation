namespace Quarterdeck.Core.Simulation;

/// <summary>
/// Mints opaque, current-state authority for one simulated dispatch intent.
/// Callers can present an admission but cannot construct or alter its trusted
/// actor, clock observation, request identity, or approval interval.
/// </summary>
public sealed class SyntheticDispatchAdmissionAuthority
{
    private readonly object _sync = new();
    private readonly string _instanceId = Guid.NewGuid().ToString("D");
    private readonly SyntheticIdentityDirectory _directory;
    private readonly ISimulationApprovalClock _clock;
    private DateTimeOffset? _lastAcceptedObservation;
    private bool _dependencyCallbackActive;
    private bool _acceptanceCommitActive;
    private bool _acceptanceValidationPending;

    public SyntheticDispatchAdmissionAuthority(
        SyntheticIdentityDirectory directory,
        ISimulationApprovalClock clock)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    internal string InstanceId => _instanceId;

    public SyntheticDispatchAdmission Capture(
        string caseId,
        long formalVersion,
        string approvalId,
        long expectedCaseRevision,
        long attemptNumber,
        string syntheticPublicRequestReference,
        SyntheticActor requester,
        SyntheticActor verifier,
        SyntheticActor approver,
        SyntheticActor issuer,
        FormalRequestFields fields,
        DateTimeOffset approvedAt,
        DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(requester);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(approver);
        ArgumentNullException.ThrowIfNull(issuer);
        ArgumentNullException.ThrowIfNull(fields);
        fields.Validate();
        RequireToken(caseId, 80, "case_id_invalid");
        RequireToken(approvalId, 80, "approval_id_invalid");
        RequireToken(syntheticPublicRequestReference, 160, "public_request_reference_invalid");
        if (formalVersion <= 0 || expectedCaseRevision < 0 || attemptNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(formalVersion), "dispatch_identity_invalid");
        RequireUtc(approvedAt);
        RequireUtc(expiresAt);
        if (expiresAt <= approvedAt)
            throw new InvalidOperationException("approval_interval_invalid");

        lock (_sync)
        {
            RequireEntryAllowed();
            var observedAt = ObserveTrustedTime(approvedAt, expiresAt);
            var currentIssuer = _directory.CaptureCurrentRoleActor(
                issuer, SyntheticWorkflowRole.ItIssuer, fields.SiteId);
            var readiness = _directory.CapturePreDispatchReadiness(
                requester, verifier, approver, currentIssuer, fields);
            _lastAcceptedObservation = observedAt;
            return new SyntheticDispatchAdmission(
                this, caseId, formalVersion, approvalId, expectedCaseRevision,
                attemptNumber, syntheticPublicRequestReference, requester, verifier,
                approver, currentIssuer, fields, approvedAt, expiresAt, observedAt, readiness);
        }
    }

    internal SyntheticDispatchRevalidation Revalidate(SyntheticDispatchAdmission admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        if (!ReferenceEquals(admission.Authority, this))
            throw new InvalidOperationException("dispatch_admission_authority_mismatch");

        lock (_sync)
        {
            RequireEntryAllowed();
            var observedAt = ObserveTrustedTime(admission.ApprovedAt, admission.ExpiresAt);
            var readiness = _directory.CapturePreDispatchReadiness(
                admission.Requester, admission.Verifier, admission.Approver,
                admission.Issuer, admission.Fields);
            _lastAcceptedObservation = observedAt;
            return new SyntheticDispatchRevalidation(observedAt, readiness.DirectoryRevision);
        }
    }

    internal void CommitSynchronizedAcceptance(
        SyntheticDispatchAdmission admission,
        ISyntheticDispatchAcceptanceCommitter committer)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(committer);
        if (!ReferenceEquals(admission.Authority, this))
            throw new InvalidOperationException("dispatch_admission_authority_mismatch");

        lock (_sync)
        {
            RequireEntryAllowed();
            if (_clock is not SyntheticSimulationApprovalClock synchronizedClock)
                throw new InvalidOperationException("simulation_acceptance_clock_unsupported");
            _acceptanceCommitActive = true;
            _acceptanceValidationPending = true;
            try
            {
                synchronizedClock.CommitSynchronizedAcceptance(
                    _directory, admission, committer);
            }
            finally
            {
                _acceptanceValidationPending = false;
                _acceptanceCommitActive = false;
            }
        }
    }

    internal void CommitValidatedAcceptance(
        SyntheticDispatchAdmission admission,
        SimulationClockObservation observation,
        SyntheticReadinessSnapshot readiness,
        ISyntheticDispatchAcceptanceCommitter committer)
    {
        if (!_acceptanceCommitActive
            || !_acceptanceValidationPending
            || !ReferenceEquals(admission.Authority, this))
            throw new InvalidOperationException("simulation_acceptance_boundary_invalid");
        _acceptanceValidationPending = false;
        var observedAt = ValidateTrustedTime(
            observation, admission.ApprovedAt, admission.ExpiresAt);
        if (readiness.DirectoryRevision != admission.Readiness.DirectoryRevision
            || readiness.SubjectAccountId != admission.Readiness.SubjectAccountId
            || readiness.IssuerAccountId != admission.Readiness.IssuerAccountId
            || !string.Equals(readiness.FakeDeviceId,
                admission.Readiness.FakeDeviceId, StringComparison.Ordinal)
            || !string.Equals(readiness.FakeAuthority,
                admission.Readiness.FakeAuthority, StringComparison.Ordinal))
            throw new InvalidOperationException("dispatch_readiness_revision_changed");
        committer.CommitAccepted(new SyntheticDispatchAcceptanceContext(
            observedAt, readiness.DirectoryRevision));
        _lastAcceptedObservation = observedAt;
    }

    private DateTimeOffset ObserveTrustedTime(DateTimeOffset approvedAt, DateTimeOffset expiresAt)
    {
        SimulationClockObservation observation;
        _dependencyCallbackActive = true;
        try
        {
            observation = _clock.Observe();
        }
        finally
        {
            _dependencyCallbackActive = false;
        }
        return ValidateTrustedTime(observation, approvedAt, expiresAt);
    }

    private DateTimeOffset ValidateTrustedTime(
        SimulationClockObservation observation,
        DateTimeOffset approvedAt,
        DateTimeOffset expiresAt)
    {
        if (!observation.Available)
            throw new InvalidOperationException("simulation_clock_unavailable");
        try
        {
            RequireUtc(observation.UtcNow);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException("simulation_clock_unavailable");
        }
        if (observation.UtcNow < approvedAt)
            throw new InvalidOperationException("simulation_clock_before_approval");
        if (observation.UtcNow >= expiresAt)
            throw new InvalidOperationException("approval_expired");
        if (_lastAcceptedObservation is not null
            && observation.UtcNow < _lastAcceptedObservation.Value)
            throw new InvalidOperationException("simulation_clock_moved_backward");
        return observation.UtcNow;
    }

    private void RequireEntryAllowed()
    {
        if (_dependencyCallbackActive || _acceptanceCommitActive)
            throw new InvalidOperationException("simulation_dependency_reentry");
    }

    private static void RequireUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
            throw new ArgumentException("simulation_clock_not_utc", nameof(value));
        if (value.Ticks % TimeSpan.TicksPerMillisecond != 0)
            throw new ArgumentException("simulation_clock_precision_unsupported", nameof(value));
    }

    private static void RequireToken(string value, int maximumLength, string code)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
            throw new ArgumentException(code);
    }
}

/// <summary>
/// Opaque authority for one exact simulated intent. Security-sensitive values
/// are visible only to the persistence assembly through the Core friend boundary.
/// </summary>
public sealed class SyntheticDispatchAdmission
{
    internal SyntheticDispatchAdmission(
        SyntheticDispatchAdmissionAuthority authority,
        string caseId,
        long formalVersion,
        string approvalId,
        long expectedCaseRevision,
        long attemptNumber,
        string syntheticPublicRequestReference,
        SyntheticActor requester,
        SyntheticActor verifier,
        SyntheticActor approver,
        SyntheticActor issuer,
        FormalRequestFields fields,
        DateTimeOffset approvedAt,
        DateTimeOffset expiresAt,
        DateTimeOffset observedAt,
        SyntheticReadinessSnapshot readiness)
    {
        Authority = authority;
        CaseId = caseId;
        FormalVersion = formalVersion;
        ApprovalId = approvalId;
        ExpectedCaseRevision = expectedCaseRevision;
        AttemptNumber = attemptNumber;
        SyntheticPublicRequestReference = syntheticPublicRequestReference;
        Requester = requester;
        Verifier = verifier;
        Approver = approver;
        Issuer = issuer;
        Fields = fields;
        ApprovedAt = approvedAt;
        ExpiresAt = expiresAt;
        ObservedAt = observedAt;
        Readiness = readiness;
        IdentityDigest = SyntheticDispatchAdmissionIdentity.Compute(this);
    }

    internal SyntheticDispatchAdmissionAuthority Authority { get; }
    internal string CaseId { get; }
    internal long FormalVersion { get; }
    internal string ApprovalId { get; }
    internal long ExpectedCaseRevision { get; }
    internal long AttemptNumber { get; }
    internal string SyntheticPublicRequestReference { get; }
    internal SyntheticActor Requester { get; }
    internal SyntheticActor Verifier { get; }
    internal SyntheticActor Approver { get; }
    internal SyntheticActor Issuer { get; }
    internal FormalRequestFields Fields { get; }
    internal DateTimeOffset ApprovedAt { get; }
    internal DateTimeOffset ExpiresAt { get; }
    internal DateTimeOffset ObservedAt { get; }
    internal SyntheticReadinessSnapshot Readiness { get; }
    internal string IdentityDigest { get; }

    internal SyntheticDispatchRevalidation Revalidate() => Authority.Revalidate(this);

    internal SyntheticDispatchAdmission Reissue(long expectedCaseRevision, long attemptNumber) =>
        Authority.Capture(
            CaseId, FormalVersion, ApprovalId, expectedCaseRevision, attemptNumber,
            SyntheticPublicRequestReference, Requester, Verifier, Approver, Issuer,
            Fields, ApprovedAt, ExpiresAt);
}

internal readonly record struct SyntheticDispatchRevalidation(
    DateTimeOffset ObservedAt,
    long DirectoryRevision);

/// <summary>
/// Immutable values established while the synthetic clock and directory are
/// locked. It deliberately exposes neither dependency, the admission authority,
/// nor an effect-capable service.
/// </summary>
internal readonly record struct SyntheticDispatchAcceptanceContext(
    DateTimeOffset ObservedAt,
    long DirectoryRevision);

/// <summary>
/// Trusted infrastructure boundary for the one durable journal transaction that
/// records acceptance. Synchronous execution is necessary: a signal/ack protocol
/// would release or lease the Core locks between validation and commit and reopen
/// the validity race. Implementations belong only in the friend Persistence
/// assembly, must perform no fake effect, and must return only after the journal
/// transaction has committed or thrown. Core supplies no general delegate,
/// permission flag, lock handle, directory, clock, or effect surface.
/// </summary>
internal interface ISyntheticDispatchAcceptanceCommitter
{
    void CommitAccepted(SyntheticDispatchAcceptanceContext context);
}

internal static class SyntheticDispatchAdmissionIdentity
{
    internal static string Compute(SyntheticDispatchAdmission value)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(int)];
        foreach (var item in new[]
        {
            value.Authority.InstanceId, value.CaseId,
            value.FormalVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            value.ApprovalId,
            value.ExpectedCaseRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            value.AttemptNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            value.SyntheticPublicRequestReference,
            Actor(value.Requester), Actor(value.Verifier), Actor(value.Approver), Actor(value.Issuer),
            value.Fields.SiteId.Value, value.Fields.PersonId.Value, value.Fields.AccountId.Value,
            value.Fields.IntendedUse, value.Fields.FakeDeviceId, value.Fields.Slot,
            value.Fields.SyntheticPublicRequestDigest, value.Fields.FakeAuthority,
            value.Fields.Template, value.Fields.PolicyOptionsVersion,
            value.ApprovedAt.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            value.ExpiresAt.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            value.ObservedAt.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            value.Readiness.DirectoryRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            value.Readiness.SubjectAccountId.Value, value.Readiness.IssuerAccountId.Value,
            value.Readiness.FakeDeviceId, value.Readiness.FakeAuthority
        })
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(item);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        return "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string Actor(SyntheticActor actor) => string.Join("\u001f",
        actor.PersonId.Value, actor.AccountId.Value, actor.DisplayLabel,
        actor.SiteId?.Value ?? string.Empty);
}
