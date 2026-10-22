namespace Quarterdeck.Core.Simulation;

public enum IntakeState
{
    WaitingForIt = 0,
    ReadyForItValidation = 1
}

public sealed record FormalRequestFields(
    SyntheticSiteId SiteId,
    SyntheticPersonId PersonId,
    SyntheticAccountId AccountId,
    string IntendedUse,
    string FakeDeviceId,
    string Slot,
    string SyntheticPublicRequestDigest,
    string FakeAuthority,
    string Template,
    string PolicyOptionsVersion)
{
    public const string RequiredIntendedUse = "PIV-authentication-simulation";
    public const string RequiredSlot = "9a";

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(SiteId);
        ArgumentNullException.ThrowIfNull(PersonId);
        ArgumentNullException.ThrowIfNull(AccountId);
        RequireExact(IntendedUse, RequiredIntendedUse, "intended_use_invalid");
        RequireToken(FakeDeviceId, "fake_device_invalid");
        RequireExact(Slot, RequiredSlot, "slot_invalid");
        if (SyntheticPublicRequestDigest is not { Length: 64 }
            || SyntheticPublicRequestDigest.Any(character =>
                !(character is >= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException("public_request_digest_invalid");
        }
        RequireToken(FakeAuthority, "fake_authority_invalid");
        RequireToken(Template, "template_invalid");
        RequireToken(PolicyOptionsVersion, "policy_options_version_invalid");
    }

    private static void RequireExact(string value, string expected, string code)
    {
        if (!string.Equals(value, expected, StringComparison.Ordinal))
        {
            throw new ArgumentException(code);
        }
    }

    private static void RequireToken(string value, string code)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160)
        {
            throw new ArgumentException(code);
        }
    }
}

public sealed class HrIntake
{
    private HrIntake(
        string caseId,
        SyntheticPersonId personId,
        SyntheticSiteId siteId,
        IReadOnlyList<SyntheticAccountId> candidateAccounts,
        IntakeState state,
        string requiredNextAction,
        SyntheticPersonId intakeActorPersonId,
        SyntheticAccountId intakeActorAccountId,
        string intakeActorDisplayLabel,
        long directoryRevision,
        object fixtureToken,
        SyntheticIdentityDirectory directory)
    {
        CaseId = caseId;
        PersonId = personId;
        SiteId = siteId;
        CandidateAccounts = candidateAccounts;
        State = state;
        RequiredNextAction = requiredNextAction;
        IntakeActorPersonId = intakeActorPersonId;
        IntakeActorAccountId = intakeActorAccountId;
        IntakeActorDisplayLabel = intakeActorDisplayLabel;
        DirectoryRevision = directoryRevision;
        FixtureToken = fixtureToken;
        Directory = directory;
    }

    public const string WaitingNextAction = "IT must resolve and validate the exact synthetic site/account.";

    public string CaseId { get; }
    public SyntheticPersonId PersonId { get; }
    public SyntheticSiteId SiteId { get; }
    public IReadOnlyList<SyntheticAccountId> CandidateAccounts { get; }
    public IntakeState State { get; }
    public string RequiredNextAction { get; }
    public SyntheticPersonId IntakeActorPersonId { get; }
    public SyntheticAccountId IntakeActorAccountId { get; }
    public string IntakeActorDisplayLabel { get; }
    public long DirectoryRevision { get; }

    internal object FixtureToken { get; }

    public static HrIntake Create(
        string caseId,
        SyntheticActor hrActor,
        SyntheticIdentityDirectory directory,
        SyntheticPersonId personId,
        SyntheticSiteId siteId,
        IEnumerable<SyntheticAccountId> candidateAccounts)
    {
        if (string.IsNullOrWhiteSpace(caseId)
            || caseId.Length > 80
            || caseId.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new ArgumentException("case_id_invalid", nameof(caseId));
        }

        ArgumentNullException.ThrowIfNull(hrActor);
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(personId);
        ArgumentNullException.ThrowIfNull(siteId);
        ArgumentNullException.ThrowIfNull(candidateAccounts);
        var candidates = candidateAccounts
            .Select(candidate => candidate
                ?? throw new ArgumentException("candidate_account_invalid", nameof(candidateAccounts)))
            .Distinct()
            .ToArray();
        var authority = directory.CaptureIntakeAuthority(
            hrActor,
            personId,
            siteId,
            candidates);

        return new HrIntake(
            caseId,
            personId,
            siteId,
            Array.AsReadOnly(candidates),
            authority.State,
            authority.State == IntakeState.WaitingForIt ? WaitingNextAction : string.Empty,
            authority.Actor.PersonId,
            authority.Actor.AccountId,
            authority.Actor.DisplayLabel,
            authority.DirectoryRevision,
            hrActor.FixtureToken,
            directory);
    }

    internal SyntheticIdentityDirectory Directory { get; }
}

public sealed class FormalRequestVersion
{
    private int _superseded;

    internal FormalRequestVersion(
        long version,
        FormalRequestFields fields,
        SimulationPolicySnapshot policySnapshot,
        SyntheticPersonId requesterPersonId,
        SyntheticAccountId requesterAccountId,
        string requesterDisplayLabel,
        long operationBindingInputRevision,
        string sealedRequestDigest,
        PolicyMigrationAttribution? policyMigration = null)
    {
        Version = version;
        Fields = fields;
        PolicySnapshot = policySnapshot;
        RequesterPersonId = requesterPersonId;
        RequesterAccountId = requesterAccountId;
        RequesterDisplayLabel = requesterDisplayLabel;
        OperationBindingInputRevision = operationBindingInputRevision;
        SealedRequestDigest = sealedRequestDigest;
        PolicyMigration = policyMigration;
    }

    public long Version { get; }
    public FormalRequestFields Fields { get; }
    public SimulationPolicySnapshot PolicySnapshot { get; }
    public SyntheticPersonId RequesterPersonId { get; }
    public SyntheticAccountId RequesterAccountId { get; }
    public string RequesterDisplayLabel { get; }
    public long OperationBindingInputRevision { get; }
    public string SealedRequestDigest { get; }
    public PolicyMigrationAttribution? PolicyMigration { get; }
    public bool Superseded => Volatile.Read(ref _superseded) != 0;

    internal void MarkSuperseded() => Volatile.Write(ref _superseded, 1);
}

public sealed record IdentityVerificationAttestation(
    long FormalVersion,
    SyntheticPersonId SubjectPersonId,
    SyntheticPersonId VerifierPersonId,
    SyntheticAccountId VerifierAccountId,
    string VerifierDisplayLabel);

public sealed record PolicyMigrationAttribution(
    SyntheticPersonId ActorPersonId,
    SyntheticAccountId ActorAccountId,
    string ActorDisplayLabel,
    SyntheticWorkflowRole ActorRole,
    string Reason,
    DateTimeOffset OccurredAt,
    long SourceFormalVersion,
    long ReplacementFormalVersion,
    SimulationPolicySnapshot ReplacementPolicy);

public sealed class SyntheticAccountResolution
{
    internal SyntheticAccountResolution(
        string caseId,
        SyntheticAccountId accountId,
        SyntheticPersonId resolverPersonId,
        SyntheticAccountId resolverAccountId,
        string resolverDisplayLabel,
        long directoryRevision,
        bool submittedCandidateMatched,
        string? correctionReason)
    {
        CaseId = caseId;
        AccountId = accountId;
        ResolverPersonId = resolverPersonId;
        ResolverAccountId = resolverAccountId;
        ResolverDisplayLabel = resolverDisplayLabel;
        DirectoryRevision = directoryRevision;
        SubmittedCandidateMatched = submittedCandidateMatched;
        CorrectionReason = correctionReason;
    }

    public string CaseId { get; }
    public SyntheticAccountId AccountId { get; }
    public SyntheticPersonId ResolverPersonId { get; }
    public SyntheticAccountId ResolverAccountId { get; }
    public string ResolverDisplayLabel { get; }
    public long DirectoryRevision { get; }
    public bool SubmittedCandidateMatched { get; }
    public string? CorrectionReason { get; }
}

public sealed class ExactVersionApproval
{
    private int _valid = 1;
    private int _current = 1;

    internal ExactVersionApproval(
        string approvalId,
        long formalVersion,
        FormalRequestFields approvedFields,
        SimulationPolicySnapshot approvedPolicySnapshot,
        SyntheticPersonId approverPersonId,
        SyntheticAccountId approverAccountId,
        string approverDisplayLabel,
        long directoryRevision,
        string approvedRequestDigest,
        DateTimeOffset approvedAt,
        DateTimeOffset expiresAt)
    {
        ApprovalId = approvalId;
        FormalVersion = formalVersion;
        ApprovedFields = approvedFields;
        ApprovedPolicySnapshot = approvedPolicySnapshot;
        ApproverPersonId = approverPersonId;
        ApproverAccountId = approverAccountId;
        ApproverDisplayLabel = approverDisplayLabel;
        DirectoryRevision = directoryRevision;
        ApprovedRequestDigest = approvedRequestDigest;
        ApprovedAt = approvedAt;
        ExpiresAt = expiresAt;
    }

    public string ApprovalId { get; }
    public long FormalVersion { get; }
    public FormalRequestFields ApprovedFields { get; }
    public SimulationPolicySnapshot ApprovedPolicySnapshot { get; }
    public SyntheticPersonId ApproverPersonId { get; }
    public SyntheticAccountId ApproverAccountId { get; }
    public string ApproverDisplayLabel { get; }
    public long DirectoryRevision { get; }
    public string ApprovedRequestDigest { get; }
    public DateTimeOffset ApprovedAt { get; }
    public DateTimeOffset ExpiresAt { get; }
    public bool Valid => Volatile.Read(ref _valid) != 0;
    public bool Current => Volatile.Read(ref _current) != 0;

    internal void Invalidate()
    {
        Volatile.Write(ref _valid, 0);
        Volatile.Write(ref _current, 0);
    }

    internal void MarkNotCurrent() => Volatile.Write(ref _current, 0);
}

/// <summary>
/// Pure in-memory lifecycle model. It exposes no provisioning, dispatch or provider operation.
/// </summary>
public sealed class FormalRequestLifecycle
{
    public static readonly TimeSpan ApprovalLifetime = TimeSpan.FromMinutes(15);

    private readonly object _sync = new();
    private readonly List<FormalRequestVersion> _versions = [];
    private readonly List<IdentityVerificationAttestation> _verifications = [];
    private readonly List<ExactVersionApproval> _approvals = [];
    private readonly List<SyntheticAccountResolution> _accountResolutions = [];
    private readonly ISimulationPolicySource _policySource;
    private readonly ISimulationApprovalClock _clock;
    private DateTimeOffset? _lastAcceptedClockObservation;
    private bool _dependencyCallbackActive;

    public FormalRequestLifecycle(
        HrIntake intake,
        ISimulationPolicySource policySource,
        ISimulationApprovalClock clock)
    {
        Intake = intake ?? throw new ArgumentNullException(nameof(intake));
        _policySource = policySource ?? throw new ArgumentNullException(nameof(policySource));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public HrIntake Intake { get; }
    public IntakeState CurrentIntakeState
    {
        get
        {
            lock (_sync)
            {
                var accountId = EffectiveAccountId;
                return accountId is not null
                    && Intake.Directory.IsExactAccountCurrentlyReady(
                        accountId,
                        Intake.PersonId,
                        Intake.SiteId)
                        ? IntakeState.ReadyForItValidation
                        : IntakeState.WaitingForIt;
            }
        }
    }

    public string RequiredNextAction => CurrentIntakeState == IntakeState.WaitingForIt
        ? HrIntake.WaitingNextAction
        : string.Empty;

    public IReadOnlyList<SyntheticAccountResolution> AccountResolutions
    {
        get
        {
            lock (_sync)
            {
                return _accountResolutions.ToArray();
            }
        }
    }
    public IReadOnlyList<FormalRequestVersion> Versions
    {
        get
        {
            lock (_sync)
            {
                return _versions.ToArray();
            }
        }
    }

    public IReadOnlyList<IdentityVerificationAttestation> Verifications
    {
        get
        {
            lock (_sync)
            {
                return _verifications.ToArray();
            }
        }
    }

    public IReadOnlyList<ExactVersionApproval> Approvals
    {
        get
        {
            lock (_sync)
            {
                return _approvals.ToArray();
            }
        }
    }

    public SyntheticAccountResolution ResolveWaitingForIt(
        SyntheticActor itRequester,
        SyntheticAccountId exactAccountId,
        string? correctionReason = null)
    {
        lock (_sync)
        {
            RequireTransitionEntryAllowed();
            RequireSameFixture(itRequester);
            ArgumentNullException.ThrowIfNull(exactAccountId);
            if (CurrentIntakeState == IntakeState.ReadyForItValidation)
            {
                throw new InvalidOperationException("account_resolution_not_required");
            }

            var submittedCandidateMatched = Intake.CandidateAccounts.Contains(exactAccountId);
            var resolved = Intake.Directory.ResolveExactEnabledAccount(
                itRequester,
                Intake.PersonId,
                Intake.SiteId,
                exactAccountId);
            var normalizedReason = NormalizeCorrectionReason(
                correctionReason,
                submittedCandidateMatched);
            var resolution = new SyntheticAccountResolution(
                Intake.CaseId,
                resolved.Subject.AccountId,
                resolved.Resolver.PersonId,
                resolved.Resolver.AccountId,
                resolved.Resolver.DisplayLabel,
                resolved.DirectoryRevision,
                submittedCandidateMatched,
                normalizedReason);
            SupersedeCurrentVersion();
            _accountResolutions.Add(resolution);
            return resolution;
        }
    }

    public FormalRequestVersion Seal(SyntheticActor itRequester, SyntheticActor subject, FormalRequestFields fields)
    {
        lock (_sync)
        {
            RequireTransitionEntryAllowed();
            ArgumentNullException.ThrowIfNull(itRequester);
            ArgumentNullException.ThrowIfNull(subject);
            ArgumentNullException.ThrowIfNull(fields);
            fields.Validate();
            var policySnapshot = CapturePolicy(fields.PolicyOptionsVersion);
            if (_versions.Count > 0
                && _versions[^1].Fields.PolicyOptionsVersion != fields.PolicyOptionsVersion)
            {
                throw new InvalidOperationException("policy_migration_required");
            }

            RequireSameFixture(itRequester, subject);
            var effectiveAccountId = EffectiveAccountId
                ?? throw new InvalidOperationException("account_missing_or_ambiguous");
            if (fields.SiteId != Intake.SiteId
                || fields.PersonId != Intake.PersonId
                || fields.AccountId != effectiveAccountId)
            {
                throw new InvalidOperationException("exact_subject_site_account_unresolved");
            }

            var authority = Intake.Directory.CaptureSealAuthority(
                itRequester,
                subject,
                fields.PersonId,
                fields.SiteId,
                fields.AccountId);

            SupersedeCurrentVersion();
            var version = new FormalRequestVersion(
                _versions.Count + 1L,
                fields,
                policySnapshot,
                authority.Requester.PersonId,
                authority.Requester.AccountId,
                authority.Requester.DisplayLabel,
                authority.DirectoryRevision,
                SealedRequestBinding.Compute(fields, policySnapshot, authority.DirectoryRevision));
            _versions.Add(version);
            return version;
        }
    }

    public FormalRequestVersion MigratePolicy(
        SyntheticActor itRequester,
        SyntheticActor subject,
        string reason)
    {
        lock (_sync)
        {
            RequireTransitionEntryAllowed();
            ArgumentNullException.ThrowIfNull(itRequester);
            ArgumentNullException.ThrowIfNull(subject);
            var normalizedReason = NormalizePolicyMigrationReason(reason);
            RequireSameFixture(itRequester, subject);
            var current = RequireCurrentVersion(
                _versions.LastOrDefault()?.Version
                ?? throw new InvalidOperationException("formal_version_not_found"));
            var policySnapshot = CapturePolicy();
            if (current.PolicySnapshot.HasSameContent(policySnapshot))
            {
                throw new InvalidOperationException("policy_migration_not_required");
            }

            var migratedFields = current.Fields with
            {
                PolicyOptionsVersion = policySnapshot.Version
            };
            var authority = Intake.Directory.CaptureSealAuthority(
                itRequester,
                subject,
                migratedFields.PersonId,
                migratedFields.SiteId,
                migratedFields.AccountId);

            var occurredAt = ObserveClock(
                latestRepresentable: DateTimeOffset.MaxValue.Subtract(ApprovalLifetime));
            var replacementVersion = _versions.Count + 1L;
            var attribution = new PolicyMigrationAttribution(
                authority.Requester.PersonId,
                authority.Requester.AccountId,
                authority.Requester.DisplayLabel,
                SyntheticWorkflowRole.ItFormalRequester,
                normalizedReason,
                occurredAt,
                current.Version,
                replacementVersion,
                policySnapshot);

            SupersedeCurrentVersion();
            var version = new FormalRequestVersion(
                replacementVersion,
                migratedFields,
                policySnapshot,
                authority.Requester.PersonId,
                authority.Requester.AccountId,
                authority.Requester.DisplayLabel,
                authority.DirectoryRevision,
                SealedRequestBinding.Compute(migratedFields, policySnapshot, authority.DirectoryRevision),
                attribution);
            _versions.Add(version);
            CommitClockObservation(occurredAt);
            return version;
        }
    }

    public IdentityVerificationAttestation VerifyIdentity(
        SyntheticActor verifier,
        long formalVersion)
    {
        lock (_sync)
        {
            RequireTransitionEntryAllowed();
            RequireSameFixture(verifier);
            var version = RequireCurrentVersion(formalVersion);
            var currentVerifier = Intake.Directory.CaptureCurrentRoleActor(
                verifier,
                SyntheticWorkflowRole.HrVerifier,
                version.Fields.SiteId);
            var attestation = new IdentityVerificationAttestation(
                version.Version,
                version.Fields.PersonId,
                currentVerifier.PersonId,
                currentVerifier.AccountId,
                currentVerifier.DisplayLabel);
            _verifications.Add(attestation);
            return attestation;
        }
    }

    public ExactVersionApproval Approve(
        SyntheticActor approver,
        SyntheticActor subject,
        long formalVersion,
        FormalRequestFields observedFields)
    {
        lock (_sync)
        {
            RequireTransitionEntryAllowed();
            ArgumentNullException.ThrowIfNull(approver);
            ArgumentNullException.ThrowIfNull(subject);
            RequireSameFixture(approver, subject);
            var version = RequireCurrentVersion(formalVersion);
            ArgumentNullException.ThrowIfNull(observedFields);

            if (version.Fields != observedFields)
            {
                throw new InvalidOperationException("bound_fields_changed");
            }

            observedFields.Validate();

            var verification = _verifications.LastOrDefault(item =>
                    item.FormalVersion == formalVersion
                    && item.SubjectPersonId == version.Fields.PersonId)
                ?? throw new InvalidOperationException("identity_verification_required");

            var approvedAt = ObserveClock(
                latestRepresentable: DateTimeOffset.MaxValue.Subtract(ApprovalLifetime));

            var approval = Intake.Directory.CommitApproval(
                version.RequesterAccountId,
                version.RequesterPersonId,
                verification.VerifierAccountId,
                verification.VerifierPersonId,
                approver,
                subject,
                version.Fields,
                authority =>
                {
                    MarkApprovalsNotCurrent(formalVersion);
                    var approval = new ExactVersionApproval(
                        Guid.NewGuid().ToString("D"),
                        formalVersion,
                        observedFields,
                        version.PolicySnapshot,
                        authority.Approver.PersonId,
                        authority.Approver.AccountId,
                        authority.Approver.DisplayLabel,
                        authority.DirectoryRevision,
                        version.SealedRequestDigest,
                        approvedAt,
                        approvedAt.Add(ApprovalLifetime));
                    _approvals.Add(approval);
                    return approval;
                });
            CommitClockObservation(approvedAt);
            return approval;
        }
    }

    public SyntheticReadinessSnapshot ValidatePreDispatch(
        SyntheticActor issuer,
        long formalVersion,
        string approvalId)
    {
        lock (_sync)
        {
            RequireTransitionEntryAllowed();
            ArgumentNullException.ThrowIfNull(issuer);
            RequireSameFixture(issuer);
            var version = RequireCurrentVersion(formalVersion);
            if (string.IsNullOrWhiteSpace(approvalId))
            {
                throw new InvalidOperationException("approval_not_current");
            }

            var approval = _approvals.SingleOrDefault(item =>
                item.FormalVersion == formalVersion
                && string.Equals(item.ApprovalId, approvalId, StringComparison.Ordinal));
            if (approval is null || !approval.Valid || !approval.Current)
            {
                throw new InvalidOperationException("approval_not_current");
            }
            if (!approval.ApprovedPolicySnapshot.HasSameContent(version.PolicySnapshot))
            {
                throw new InvalidOperationException("policy_version_mismatch");
            }
            if (!string.Equals(
                    approval.ApprovedRequestDigest,
                    version.SealedRequestDigest,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("sealed_request_digest_mismatch");
            }

            var now = ObserveClock(approval.ApprovedAt);
            if (now >= approval.ExpiresAt)
            {
                CommitClockObservation(now);
                throw new InvalidOperationException("approval_expired");
            }
            var verification = _verifications.LastOrDefault(item =>
                item.FormalVersion == formalVersion
                && item.SubjectPersonId == version.Fields.PersonId)
                ?? throw new InvalidOperationException("identity_verification_required");
            var requester = Intake.Directory.Resolve(version.RequesterAccountId);
            var verifier = Intake.Directory.Resolve(verification.VerifierAccountId);
            var approver = Intake.Directory.Resolve(approval.ApproverAccountId);
            var readiness = Intake.Directory.CapturePreDispatchReadiness(
                requester,
                verifier,
                approver,
                issuer,
                version.Fields);
            CommitClockObservation(now);
            return readiness;
        }
    }

    private FormalRequestVersion RequireCurrentVersion(long formalVersion)
    {
        var version = _versions.SingleOrDefault(item => item.Version == formalVersion)
            ?? throw new InvalidOperationException("formal_version_not_found");
        if (version.Superseded || !ReferenceEquals(version, _versions[^1]))
        {
            throw new InvalidOperationException("formal_version_superseded");
        }

        return version;
    }

    private void RequireSameFixture(params SyntheticActor[] actors)
    {
        foreach (var actor in actors)
        {
            if (!ReferenceEquals(actor.FixtureToken, IntakeActorFixtureToken))
            {
                throw new InvalidOperationException("synthetic_fixture_mismatch");
            }
        }
    }

    private object IntakeActorFixtureToken => Intake.FixtureToken;

    private SimulationPolicySnapshot CapturePolicy(string? requiredVersion = null)
    {
        SimulationPolicySnapshot snapshot;
        _dependencyCallbackActive = true;
        try
        {
            snapshot = _policySource.CaptureCurrent()
                ?? throw new InvalidOperationException("policy_version_mismatch");
        }
        finally
        {
            _dependencyCallbackActive = false;
        }

        var stable = SimulationPolicySnapshot.CreateClassified(
            snapshot.AuthorityId,
            snapshot.Version,
            snapshot.Selections);
        var boundAuthorityId = _versions.FirstOrDefault()?.PolicySnapshot.AuthorityId;
        if (boundAuthorityId is not null
            && !string.Equals(
                stable.AuthorityId,
                boundAuthorityId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("policy_authority_mismatch");
        }
        if (requiredVersion is not null
            && !string.Equals(stable.Version, requiredVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("policy_version_mismatch");
        }

        var priorWithSameVersion = _versions
            .Select(version => version.PolicySnapshot)
            .FirstOrDefault(policy => policy.Version == stable.Version);
        if (priorWithSameVersion is not null
            && !priorWithSameVersion.HasSameContent(stable))
        {
            throw new InvalidOperationException("policy_version_mismatch");
        }

        return stable;
    }

    private DateTimeOffset ObserveClock(
        DateTimeOffset? approvedAt = null,
        DateTimeOffset? latestRepresentable = null)
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

        if (!observation.Available || observation.UtcNow.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("simulation_clock_unavailable");
        }

        if (latestRepresentable is not null
            && observation.UtcNow > latestRepresentable.Value)
        {
            throw new InvalidOperationException("simulation_clock_unavailable");
        }

        if (approvedAt is not null && observation.UtcNow < approvedAt.Value)
        {
            throw new InvalidOperationException("simulation_clock_before_approval");
        }

        if (_lastAcceptedClockObservation is not null
            && observation.UtcNow < _lastAcceptedClockObservation.Value)
        {
            throw new InvalidOperationException("simulation_clock_moved_backward");
        }

        return observation.UtcNow;
    }

    private void CommitClockObservation(DateTimeOffset observation)
    {
        if (_lastAcceptedClockObservation is not null
            && observation < _lastAcceptedClockObservation.Value)
        {
            throw new InvalidOperationException("simulation_clock_moved_backward");
        }

        _lastAcceptedClockObservation = observation;
    }

    private void RequireTransitionEntryAllowed()
    {
        if (_dependencyCallbackActive)
        {
            throw new InvalidOperationException("simulation_dependency_reentry");
        }
    }

    private static string NormalizePolicyMigrationReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)
            || reason.Length > 80
            || reason.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new ArgumentException("policy_migration_reason_invalid", nameof(reason));
        }

        return reason;
    }

    private void MarkApprovalsNotCurrent(long formalVersion)
    {
        foreach (var approval in _approvals.Where(item =>
                     item.FormalVersion == formalVersion && item.Current))
        {
            approval.MarkNotCurrent();
        }
    }

    private SyntheticAccountId? EffectiveAccountId => _accountResolutions.Count > 0
        ? _accountResolutions[^1].AccountId
        : Intake.State == IntakeState.ReadyForItValidation
            && Intake.CandidateAccounts.Count == 1
            ? Intake.CandidateAccounts[0]
            : null;

    private static string? NormalizeCorrectionReason(
        string? correctionReason,
        bool submittedCandidateMatched)
    {
        if (correctionReason is null)
        {
            if (!submittedCandidateMatched)
            {
                throw new InvalidOperationException("account_correction_reason_required");
            }

            return null;
        }

        var normalized = correctionReason.Trim();
        if (normalized.Length == 0
            || normalized.Length > 80
            || normalized.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new ArgumentException("account_correction_reason_invalid", nameof(correctionReason));
        }

        return normalized;
    }

    private void SupersedeCurrentVersion()
    {
        if (_versions.Count == 0)
        {
            return;
        }

        var current = _versions[^1];
        current.MarkSuperseded();
        InvalidateApprovals(current.Version);
    }

    private void InvalidateApprovals(long formalVersion)
    {
        for (var index = 0; index < _approvals.Count; index++)
        {
            if (_approvals[index].FormalVersion == formalVersion && _approvals[index].Valid)
            {
                _approvals[index].Invalidate();
            }
        }
    }
}
