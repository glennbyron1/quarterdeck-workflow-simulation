namespace Quarterdeck.Core.Simulation;

public sealed record SyntheticPersonId
{
    public SyntheticPersonId(string value)
    {
        Value = SyntheticIdentifierPolicy.Require(value, nameof(value));
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record SyntheticAccountId
{
    public SyntheticAccountId(string value)
    {
        Value = SyntheticIdentifierPolicy.Require(value, nameof(value));
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record SyntheticSiteId
{
    public SyntheticSiteId(string value)
    {
        Value = SyntheticIdentifierPolicy.Require(value, nameof(value));
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public enum SyntheticWorkflowRole
{
    HrIntake,
    HrVerifier,
    HrApprover,
    ItFormalRequester,
    ItIssuer,
    ItDeliverer
}

public enum SyntheticAccountState
{
    Unknown = 0,
    Enabled = 1,
    Disabled = 2,
    Ambiguous = 3
}

public enum SyntheticRoleState
{
    Unknown = 0,
    Granted = 1,
    Removed = 2,
    Ambiguous = 3
}

public enum SyntheticContainmentState
{
    Unknown = 0,
    Clear = 1,
    Contained = 2,
    Ambiguous = 3
}

public enum SyntheticDeviceQualificationState
{
    Unknown = 0,
    Qualified = 1,
    Disqualified = 2,
    Ambiguous = 3
}

public enum SyntheticAuthorityAvailabilityState
{
    Unknown = 0,
    Available = 1,
    Unavailable = 2,
    Ambiguous = 3
}

internal static class SyntheticIdentifierPolicy
{
    public static string Require(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 80
            || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new ArgumentException("Synthetic identifiers must be bounded ASCII tokens.", parameterName);
        }

        return value;
    }
}

public sealed record SyntheticIdentityBinding(
    SyntheticPersonId PersonId,
    SyntheticAccountId AccountId,
    string DisplayLabel,
    SyntheticSiteId? SiteId = null,
    IReadOnlySet<SyntheticWorkflowRole>? Roles = null,
    bool AccountEnabled = true);

/// <summary>
/// A revisioned in-memory fixture that owns synthetic identity and readiness state.
/// Actors are immutable observations; consequential checks always return here.
/// </summary>
public sealed class SyntheticIdentityDirectory
{
    private sealed class AccountEntry
    {
        public AccountEntry(SyntheticIdentityBinding binding)
        {
            Binding = binding;
            AccountState = binding.AccountEnabled
                ? SyntheticAccountState.Enabled
                : SyntheticAccountState.Disabled;
            ContainmentState = SyntheticContainmentState.Unknown;
            RoleStates = Enum.GetValues<SyntheticWorkflowRole>()
                .ToDictionary(
                    role => role,
                    role => binding.Roles?.Contains(role) == true
                        ? SyntheticRoleState.Granted
                        : SyntheticRoleState.Removed);
        }

        public SyntheticIdentityBinding Binding { get; }
        public SyntheticAccountState AccountState { get; set; }
        public SyntheticContainmentState ContainmentState { get; set; }
        public Dictionary<SyntheticWorkflowRole, SyntheticRoleState> RoleStates { get; }
    }

    private readonly object _sync = new();
    private readonly object _fixtureToken = new();
    private readonly Dictionary<SyntheticAccountId, AccountEntry> _byAccount;
    private readonly Dictionary<string, SyntheticDeviceQualificationState> _deviceStates =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, SyntheticAuthorityAvailabilityState> _authorityStates =
        new(StringComparer.Ordinal);
    private long _revision = 1;
    private bool _acceptanceCommitActive;

    private SyntheticIdentityDirectory(
        Dictionary<SyntheticAccountId, AccountEntry> byAccount)
    {
        _byAccount = byAccount;
    }

    public long Revision
    {
        get
        {
            lock (_sync)
            {
                return _revision;
            }
        }
    }

    internal int AccountCount
    {
        get
        {
            lock (_sync)
            {
                return _byAccount.Count;
            }
        }
    }

    public static SyntheticIdentityDirectory Create(
        IEnumerable<SyntheticIdentityBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var byAccount = new Dictionary<SyntheticAccountId, AccountEntry>();

        foreach (var binding in bindings)
        {
            ArgumentNullException.ThrowIfNull(binding);
            ArgumentNullException.ThrowIfNull(binding.PersonId);
            ArgumentNullException.ThrowIfNull(binding.AccountId);
            SyntheticDisplayLabelPolicy.Require(binding.DisplayLabel, nameof(bindings));
            var bindingSnapshot = new SyntheticIdentityBinding(
                binding.PersonId,
                binding.AccountId,
                binding.DisplayLabel,
                binding.SiteId,
                new HashSet<SyntheticWorkflowRole>(
                    binding.Roles ?? new HashSet<SyntheticWorkflowRole>()),
                binding.AccountEnabled);

            if (!byAccount.TryAdd(bindingSnapshot.AccountId, new AccountEntry(bindingSnapshot)))
            {
                throw new ArgumentException(
                    "synthetic_account_binding_duplicate",
                    nameof(bindings));
            }
        }

        if (byAccount.Count == 0)
        {
            throw new ArgumentException(
                "The synthetic identity directory must not be empty.",
                nameof(bindings));
        }

        return new SyntheticIdentityDirectory(byAccount);
    }

    public SyntheticActor Resolve(SyntheticAccountId accountId)
    {
        ArgumentNullException.ThrowIfNull(accountId);
        lock (_sync)
        {
            return ResolveEntry(RequireEntry(accountId));
        }
    }

    internal T RegisterSimulationSubjectAccount<T>(
        SyntheticActor registrar,
        SyntheticPersonId personId,
        SyntheticSiteId siteId,
        SyntheticAccountId accountId,
        string displayLabel,
        Func<T> persistResolution)
    {
        ArgumentNullException.ThrowIfNull(personId);
        ArgumentNullException.ThrowIfNull(siteId);
        ArgumentNullException.ThrowIfNull(accountId);
        SyntheticDisplayLabelPolicy.Require(displayLabel, nameof(displayLabel));
        lock (_sync)
        {
            RequireMutationAllowed();
            RequireCurrentRoleActor(registrar,
                SyntheticWorkflowRole.ItFormalRequester, siteId);
            if (_byAccount.ContainsKey(accountId))
                throw new InvalidOperationException("synthetic_account_already_registered");
            var entry = new AccountEntry(new SyntheticIdentityBinding(
                personId, accountId, displayLabel, siteId));
            entry.ContainmentState = SyntheticContainmentState.Clear;
            _byAccount.Add(accountId, entry);
            ++_revision;
            try { return persistResolution(); }
            catch
            {
                _byAccount.Remove(accountId);
                ++_revision;
                throw;
            }
        }
    }

    internal void RestoreSimulationSubjectAccount(
        SyntheticPersonId personId,
        SyntheticSiteId siteId,
        SyntheticAccountId accountId)
    {
        lock (_sync)
        {
            RequireMutationAllowed();
            if (_byAccount.TryGetValue(accountId, out var existing))
            {
                if (existing.Binding.PersonId != personId || existing.Binding.SiteId != siteId)
                    throw new InvalidOperationException("synthetic_fixture_restore_conflict");
                return;
            }
            var entry = new AccountEntry(new SyntheticIdentityBinding(
                personId, accountId, personId.Value, siteId));
            entry.ContainmentState = SyntheticContainmentState.Clear;
            _byAccount.Add(accountId, entry);
            ++_revision;
        }
    }

    internal long SetAccountState(SyntheticAccountId accountId, SyntheticAccountState state)
    {
        ArgumentNullException.ThrowIfNull(accountId);
        lock (_sync)
        {
            RequireMutationAllowed();
            RequireEntry(accountId).AccountState = state;
            return ++_revision;
        }
    }

    internal long SetRoleState(
        SyntheticAccountId accountId,
        SyntheticWorkflowRole role,
        SyntheticRoleState state)
    {
        ArgumentNullException.ThrowIfNull(accountId);
        lock (_sync)
        {
            RequireMutationAllowed();
            RequireEntry(accountId).RoleStates[role] = state;
            return ++_revision;
        }
    }

    internal long SetContainmentState(
        SyntheticAccountId accountId,
        SyntheticContainmentState state)
    {
        ArgumentNullException.ThrowIfNull(accountId);
        lock (_sync)
        {
            RequireMutationAllowed();
            RequireEntry(accountId).ContainmentState = state;
            return ++_revision;
        }
    }

    internal long SetDeviceQualificationState(
        string fakeDeviceId,
        SyntheticDeviceQualificationState state)
    {
        RequireResourceToken(fakeDeviceId, nameof(fakeDeviceId));
        lock (_sync)
        {
            RequireMutationAllowed();
            _deviceStates[fakeDeviceId] = state;
            return ++_revision;
        }
    }

    internal long SetAuthorityAvailabilityState(
        string fakeAuthority,
        SyntheticAuthorityAvailabilityState state)
    {
        RequireResourceToken(fakeAuthority, nameof(fakeAuthority));
        lock (_sync)
        {
            RequireMutationAllowed();
            _authorityStates[fakeAuthority] = state;
            return ++_revision;
        }
    }

    internal SyntheticIntakeAuthority CaptureIntakeAuthority(
        SyntheticActor actor,
        SyntheticPersonId personId,
        SyntheticSiteId siteId,
        IReadOnlyList<SyntheticAccountId> submittedCandidates)
    {
        ArgumentNullException.ThrowIfNull(personId);
        ArgumentNullException.ThrowIfNull(siteId);
        ArgumentNullException.ThrowIfNull(submittedCandidates);
        lock (_sync)
        {
            var currentActor = RequireCurrentRoleActor(
                actor,
                SyntheticWorkflowRole.HrIntake,
                siteId);
            var ready = submittedCandidates.Count == 1
                && IsExactEnabledSubjectAccount(
                    submittedCandidates[0],
                    personId,
                    siteId);
            return new SyntheticIntakeAuthority(
                currentActor,
                ready ? IntakeState.ReadyForItValidation : IntakeState.WaitingForIt,
                _revision);
        }
    }

    internal SyntheticAccountResolutionAuthority ResolveExactEnabledAccount(
        SyntheticActor resolver,
        SyntheticPersonId personId,
        SyntheticSiteId siteId,
        SyntheticAccountId accountId)
    {
        ArgumentNullException.ThrowIfNull(personId);
        ArgumentNullException.ThrowIfNull(siteId);
        ArgumentNullException.ThrowIfNull(accountId);
        lock (_sync)
        {
            var currentResolver = RequireCurrentRoleActor(
                resolver,
                SyntheticWorkflowRole.ItFormalRequester,
                siteId);
            if (!_byAccount.TryGetValue(accountId, out var entry))
            {
                throw new InvalidOperationException("exact_subject_site_account_unresolved");
            }

            if (entry.Binding.PersonId != personId || entry.Binding.SiteId != siteId)
            {
                throw new InvalidOperationException("exact_subject_site_account_unresolved");
            }
            RequireSubjectAccountEnabled(entry);

            return new SyntheticAccountResolutionAuthority(
                currentResolver,
                ResolveEntry(entry),
                _revision);
        }
    }

    internal SyntheticActor CaptureCurrentRoleActor(
        SyntheticActor actor,
        SyntheticWorkflowRole role,
        SyntheticSiteId siteId)
    {
        ArgumentNullException.ThrowIfNull(siteId);
        lock (_sync)
        {
            return RequireCurrentRoleActor(actor, role, siteId);
        }
    }

    public SyntheticReconciliationAuthorization AuthorizeReconciliation(
        SyntheticActor actor, SyntheticSiteId siteId)
    {
        ArgumentNullException.ThrowIfNull(siteId);
        lock (_sync)
        {
            var current = RequireCurrentRoleActor(actor,
                SyntheticWorkflowRole.ItIssuer, siteId);
            return new SyntheticReconciliationAuthorization(this, current, siteId);
        }
    }

    internal SyntheticActor RevalidateReconciliation(
        SyntheticReconciliationAuthorization authorization)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(authorization.Directory, this))
                throw new InvalidOperationException("synthetic_fixture_mismatch");
            return RequireCurrentRoleIdentity(authorization.AccountId,
                authorization.PersonId, SyntheticWorkflowRole.ItIssuer,
                authorization.SiteId);
        }
    }

    internal SyntheticSealAuthority CaptureSealAuthority(
        SyntheticActor requester,
        SyntheticActor subject,
        SyntheticPersonId personId,
        SyntheticSiteId siteId,
        SyntheticAccountId accountId)
    {
        lock (_sync)
        {
            var currentRequester = RequireCurrentRoleActor(
                requester,
                SyntheticWorkflowRole.ItFormalRequester,
                siteId);
            RequireSameFixture(subject);
            var entry = RequireEntry(subject.AccountId);
            if (subject.PersonId != personId
                || subject.SiteId != siteId
                || subject.AccountId != accountId)
            {
                throw new InvalidOperationException("exact_subject_site_account_unresolved");
            }
            RequireSubjectAccountEnabled(entry);

            if (entry.Binding.PersonId != personId || entry.Binding.SiteId != siteId)
            {
                throw new InvalidOperationException("exact_subject_site_account_unresolved");
            }

            return new SyntheticSealAuthority(currentRequester, ResolveEntry(entry), _revision);
        }
    }

    internal bool IsExactAccountCurrentlyReady(
        SyntheticAccountId accountId,
        SyntheticPersonId personId,
        SyntheticSiteId siteId)
    {
        ArgumentNullException.ThrowIfNull(accountId);
        ArgumentNullException.ThrowIfNull(personId);
        ArgumentNullException.ThrowIfNull(siteId);
        lock (_sync)
        {
            return IsExactEnabledSubjectAccount(accountId, personId, siteId);
        }
    }

    internal T CommitApproval<T>(
        SyntheticAccountId requesterAccountId,
        SyntheticPersonId requesterPersonId,
        SyntheticAccountId verifierAccountId,
        SyntheticPersonId verifierPersonId,
        SyntheticActor approver,
        SyntheticActor subject,
        FormalRequestFields fields,
        Func<SyntheticApprovalAuthority, T> commit)
    {
        ArgumentNullException.ThrowIfNull(requesterAccountId);
        ArgumentNullException.ThrowIfNull(requesterPersonId);
        ArgumentNullException.ThrowIfNull(verifierAccountId);
        ArgumentNullException.ThrowIfNull(verifierPersonId);
        ArgumentNullException.ThrowIfNull(approver);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(commit);

        lock (_sync)
        {
            var requester = RequireCurrentRoleIdentity(
                requesterAccountId,
                requesterPersonId,
                SyntheticWorkflowRole.ItFormalRequester,
                fields.SiteId);
            var verifier = RequireCurrentRoleIdentity(
                verifierAccountId,
                verifierPersonId,
                SyntheticWorkflowRole.HrVerifier,
                fields.SiteId);
            var currentApprover = RequireCurrentRoleActor(
                approver,
                SyntheticWorkflowRole.HrApprover,
                fields.SiteId);
            RequireSameFixture(subject);
            var subjectEntry = RequireEntry(subject.AccountId);
            if (subject.PersonId != fields.PersonId
                || subject.SiteId != fields.SiteId
                || subject.AccountId != fields.AccountId
                || subjectEntry.Binding.PersonId != fields.PersonId
                || subjectEntry.Binding.SiteId != fields.SiteId)
            {
                throw new InvalidOperationException("exact_subject_site_account_unresolved");
            }
            RequireSubjectAccountEnabled(subjectEntry);

            if (requester.PersonId == currentApprover.PersonId)
            {
                throw new InvalidOperationException("requester_approver_same_person");
            }

            RequireContainmentClear(subjectEntry);
            RequireDeviceQualified(fields.FakeDeviceId);
            RequireAuthorityAvailable(fields.FakeAuthority);

            var authority = new SyntheticApprovalAuthority(
                _revision,
                requester,
                verifier,
                currentApprover,
                ResolveEntry(subjectEntry));
            ApprovalSynchronizationGate?.PauseAfterCapture();
            return commit(authority);
        }
    }

    internal SyntheticApprovalSynchronizationGate? ApprovalSynchronizationGate { get; set; }

    internal SyntheticReadinessSnapshot CapturePreDispatchReadiness(
        SyntheticActor requester,
        SyntheticActor verifier,
        SyntheticActor approver,
        SyntheticActor issuer,
        FormalRequestFields fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        lock (_sync)
        {
            RequireCurrentRoleActor(
                requester,
                SyntheticWorkflowRole.ItFormalRequester,
                fields.SiteId);
            RequireCurrentRoleActor(
                verifier,
                SyntheticWorkflowRole.HrVerifier,
                fields.SiteId);
            RequireCurrentRoleActor(
                approver,
                SyntheticWorkflowRole.HrApprover,
                fields.SiteId);
            RequireCurrentRoleActor(
                issuer,
                SyntheticWorkflowRole.ItIssuer,
                fields.SiteId);

            var subject = RequireEntry(fields.AccountId);
            if (subject.Binding.PersonId != fields.PersonId
                || subject.Binding.SiteId != fields.SiteId)
            {
                throw new InvalidOperationException("exact_subject_site_account_unresolved");
            }

            RequireSubjectAccountEnabled(subject);
            RequireContainmentClear(subject);
            RequireDeviceQualified(fields.FakeDeviceId);
            RequireAuthorityAvailable(fields.FakeAuthority);

            return new SyntheticReadinessSnapshot(
                _revision,
                fields.AccountId,
                issuer.AccountId,
                fields.FakeDeviceId,
                fields.FakeAuthority);
        }
    }

    internal void CommitSynchronizedAcceptance(
        SyntheticDispatchAdmission admission,
        SimulationClockObservation observation,
        ISyntheticDispatchAcceptanceCommitter committer)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(committer);
        lock (_sync)
        {
            RequireMutationAllowed();
            _acceptanceCommitActive = true;
            try
            {
                admission.Authority.CommitValidatedAcceptance(
                    admission, observation, CapturePreDispatchReadiness(
                        admission.Requester, admission.Verifier, admission.Approver,
                        admission.Issuer, admission.Fields), committer);
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

    private AccountEntry RequireEntry(SyntheticAccountId accountId)
    {
        if (!_byAccount.TryGetValue(accountId, out var entry))
        {
            throw new KeyNotFoundException("synthetic_account_not_found");
        }

        return entry;
    }

    private SyntheticActor RequireCurrentRoleActor(
        SyntheticActor actor,
        SyntheticWorkflowRole role,
        SyntheticSiteId siteId)
    {
        RequireSameFixture(actor);
        var entry = RequireEntry(actor.AccountId);
        if (entry.Binding.PersonId != actor.PersonId)
        {
            throw new InvalidOperationException("actor_identity_mismatch");
        }

        return RequireCurrentRoleEntry(entry, role, siteId);
    }

    private SyntheticActor RequireCurrentRoleIdentity(
        SyntheticAccountId accountId,
        SyntheticPersonId personId,
        SyntheticWorkflowRole role,
        SyntheticSiteId siteId)
    {
        var entry = RequireEntry(accountId);
        if (entry.Binding.PersonId != personId)
        {
            throw new InvalidOperationException("actor_identity_mismatch");
        }

        return RequireCurrentRoleEntry(entry, role, siteId);
    }

    private SyntheticActor RequireCurrentRoleEntry(
        AccountEntry entry,
        SyntheticWorkflowRole role,
        SyntheticSiteId siteId)
    {
        RequireActorAccountEnabled(entry);
        if (entry.Binding.SiteId != siteId)
        {
            throw new InvalidOperationException("actor_site_scope_mismatch");
        }

        RequireRole(entry, role);
        return ResolveEntry(entry);
    }

    private bool IsExactEnabledSubjectAccount(
        SyntheticAccountId accountId,
        SyntheticPersonId personId,
        SyntheticSiteId siteId) =>
        _byAccount.TryGetValue(accountId, out var entry)
        && entry.Binding.PersonId == personId
        && entry.Binding.SiteId == siteId
        && entry.AccountState == SyntheticAccountState.Enabled;

    private SyntheticActor ResolveEntry(AccountEntry entry)
    {
        var binding = entry.Binding;
        var roles = entry.RoleStates
            .Where(item => item.Value == SyntheticRoleState.Granted)
            .Select(item => item.Key)
            .ToHashSet();
        return new SyntheticActor(
            this,
            _fixtureToken,
            binding.PersonId,
            binding.AccountId,
            binding.DisplayLabel,
            binding.SiteId,
            roles,
            entry.AccountState == SyntheticAccountState.Enabled,
            _revision);
    }

    private void RequireSameFixture(SyntheticActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!ReferenceEquals(_fixtureToken, actor.FixtureToken)
            || !ReferenceEquals(this, actor.Directory))
        {
            throw new InvalidOperationException("synthetic_fixture_mismatch");
        }
    }

    private static void RequireActorAccountEnabled(AccountEntry entry)
    {
        switch (entry.AccountState)
        {
            case SyntheticAccountState.Enabled:
                return;
            case SyntheticAccountState.Disabled:
                throw new InvalidOperationException("actor_account_disabled");
            case SyntheticAccountState.Ambiguous:
                throw new InvalidOperationException("actor_account_state_ambiguous");
            default:
                throw new InvalidOperationException("actor_account_state_unknown");
        }
    }

    private static void RequireSubjectAccountEnabled(AccountEntry entry)
    {
        switch (entry.AccountState)
        {
            case SyntheticAccountState.Enabled:
                return;
            case SyntheticAccountState.Disabled:
                throw new InvalidOperationException("subject_disabled");
            case SyntheticAccountState.Ambiguous:
                throw new InvalidOperationException("subject_account_state_ambiguous");
            default:
                throw new InvalidOperationException("subject_account_state_unknown");
        }
    }

    private static void RequireRole(AccountEntry entry, SyntheticWorkflowRole role)
    {
        switch (entry.RoleStates[role])
        {
            case SyntheticRoleState.Granted:
                return;
            case SyntheticRoleState.Removed:
                throw new InvalidOperationException("required_role_missing");
            case SyntheticRoleState.Ambiguous:
                throw new InvalidOperationException("role_state_ambiguous");
            default:
                throw new InvalidOperationException("role_state_unknown");
        }
    }

    private static void RequireContainmentClear(AccountEntry entry)
    {
        switch (entry.ContainmentState)
        {
            case SyntheticContainmentState.Clear:
                return;
            case SyntheticContainmentState.Contained:
                throw new InvalidOperationException("subject_contained");
            case SyntheticContainmentState.Ambiguous:
                throw new InvalidOperationException("containment_state_ambiguous");
            default:
                throw new InvalidOperationException("containment_state_unknown");
        }
    }

    private void RequireDeviceQualified(string fakeDeviceId)
    {
        var state = _deviceStates.GetValueOrDefault(
            fakeDeviceId,
            SyntheticDeviceQualificationState.Unknown);
        switch (state)
        {
            case SyntheticDeviceQualificationState.Qualified:
                return;
            case SyntheticDeviceQualificationState.Disqualified:
                throw new InvalidOperationException("device_not_qualified");
            case SyntheticDeviceQualificationState.Ambiguous:
                throw new InvalidOperationException("device_state_ambiguous");
            default:
                throw new InvalidOperationException("device_state_unknown");
        }
    }

    private void RequireAuthorityAvailable(string fakeAuthority)
    {
        var state = _authorityStates.GetValueOrDefault(
            fakeAuthority,
            SyntheticAuthorityAvailabilityState.Unknown);
        switch (state)
        {
            case SyntheticAuthorityAvailabilityState.Available:
                return;
            case SyntheticAuthorityAvailabilityState.Unavailable:
                throw new InvalidOperationException("authority_unavailable");
            case SyntheticAuthorityAvailabilityState.Ambiguous:
                throw new InvalidOperationException("authority_state_ambiguous");
            default:
                throw new InvalidOperationException("authority_state_unknown");
        }
    }

    private static void RequireResourceToken(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160)
        {
            throw new ArgumentException("Synthetic resource tokens must be present and bounded.", parameterName);
        }
    }

}

internal sealed record SyntheticAccountResolutionAuthority(
    SyntheticActor Resolver,
    SyntheticActor Subject,
    long DirectoryRevision);

internal sealed record SyntheticIntakeAuthority(
    SyntheticActor Actor,
    IntakeState State,
    long DirectoryRevision);

internal sealed record SyntheticSealAuthority(
    SyntheticActor Requester,
    SyntheticActor Subject,
    long DirectoryRevision);

internal sealed record SyntheticApprovalAuthority(
    long DirectoryRevision,
    SyntheticActor Requester,
    SyntheticActor Verifier,
    SyntheticActor Approver,
    SyntheticActor Subject);

/// <summary>
/// Deterministic test instrumentation that can pause approval while the directory
/// lock is held without executing a caller-supplied delegate in the critical section.
/// </summary>
internal sealed class SyntheticApprovalSynchronizationGate : IDisposable
{
    private readonly ManualResetEventSlim _captured = new(false);
    private readonly ManualResetEventSlim _release = new(false);

    public bool WaitUntilCaptured(TimeSpan timeout) => _captured.Wait(timeout);

    public void Release() => _release.Set();

    internal void PauseAfterCapture()
    {
        _captured.Set();
        if (!_release.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("approval_test_release_timeout");
        }
    }

    public void Dispose()
    {
        _captured.Dispose();
        _release.Dispose();
    }
}

public sealed record SyntheticReadinessSnapshot(
    long DirectoryRevision,
    SyntheticAccountId SubjectAccountId,
    SyntheticAccountId IssuerAccountId,
    string FakeDeviceId,
    string FakeAuthority);

internal static class SyntheticDisplayLabelPolicy
{
    public static string Require(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 120)
        {
            throw new ArgumentException(
                "Synthetic display labels must be present and bounded.",
                parameterName);
        }

        return value;
    }
}

public sealed class SyntheticActor
{
    private readonly SyntheticIdentityDirectory _directory;
    private readonly object _fixtureToken;
    private readonly IReadOnlySet<SyntheticWorkflowRole> _roles;

    internal SyntheticActor(
        SyntheticIdentityDirectory directory,
        object fixtureToken,
        SyntheticPersonId personId,
        SyntheticAccountId accountId,
        string displayLabel,
        SyntheticSiteId? siteId,
        IReadOnlySet<SyntheticWorkflowRole> roles,
        bool accountEnabled,
        long observedAtRevision)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _fixtureToken = fixtureToken ?? throw new ArgumentNullException(nameof(fixtureToken));
        PersonId = personId ?? throw new ArgumentNullException(nameof(personId));
        AccountId = accountId ?? throw new ArgumentNullException(nameof(accountId));
        DisplayLabel = SyntheticDisplayLabelPolicy.Require(displayLabel, nameof(displayLabel));
        SiteId = siteId;
        _roles = new HashSet<SyntheticWorkflowRole>(
            roles ?? throw new ArgumentNullException(nameof(roles)));
        AccountEnabled = accountEnabled;
        ObservedAtRevision = observedAtRevision;
    }

    public SyntheticPersonId PersonId { get; }
    public SyntheticAccountId AccountId { get; }
    public string DisplayLabel { get; }
    public SyntheticSiteId? SiteId { get; }
    public bool AccountEnabled { get; }
    public long ObservedAtRevision { get; }

    public bool HasRole(SyntheticWorkflowRole role) => _roles.Contains(role);

    internal object FixtureToken => _fixtureToken;
    internal SyntheticIdentityDirectory Directory => _directory;

    internal bool SharesFixtureWith(SyntheticActor other) =>
        ReferenceEquals(_fixtureToken, other._fixtureToken);
}

public sealed class SyntheticReconciliationAuthorization
{
    internal SyntheticReconciliationAuthorization(SyntheticIdentityDirectory directory,
        SyntheticActor actor, SyntheticSiteId siteId)
    {
        Directory = directory;
        PersonId = actor.PersonId;
        AccountId = actor.AccountId;
        DisplayLabel = actor.DisplayLabel;
        SiteId = siteId;
    }

    internal SyntheticIdentityDirectory Directory { get; }
    internal SyntheticPersonId PersonId { get; }
    internal SyntheticAccountId AccountId { get; }
    internal string DisplayLabel { get; }
    internal SyntheticSiteId SiteId { get; }
    internal SyntheticActor Revalidate() => Directory.RevalidateReconciliation(this);
}

public static class SyntheticActorSeparationPolicy
{
    public static bool IsDistinctPerson(SyntheticActor requester, SyntheticActor approver)
    {
        ArgumentNullException.ThrowIfNull(requester);
        ArgumentNullException.ThrowIfNull(approver);
        if (!requester.SharesFixtureWith(approver))
        {
            throw new InvalidOperationException("synthetic_fixture_mismatch");
        }

        return requester.PersonId != approver.PersonId;
    }
}

/// <summary>
/// Stable identifiers and role bindings for the eight owner-approved fictional people.
/// Login labels in the seed manifest remain deployment data, not authorization identity.
/// </summary>
public static class CanonicalEightPersonSyntheticFixture
{
    public const string HrPrimaryAccount = "account-test-hr01";
    public const string HrAlternateAccount = "account-test-hr02";
    public const string ItPrimaryAccount = "account-test-it01";
    public const string ItAlternateAccount = "account-test-it02";
    public const string EmployeePrimaryAccount = "account-test-emp01";
    public const string EmployeeAlternateAccount = "account-test-emp02";
    public const string ControlAccount = "account-test-ctrl01";
    public const string InfrastructureAdministratorAccount = "account-test-admin01";

    public static SyntheticIdentityDirectory Create(SyntheticSiteId siteId)
    {
        ArgumentNullException.ThrowIfNull(siteId);
        return SyntheticIdentityDirectory.Create(
        [
            Binding(
                "person-avery-reed",
                HrPrimaryAccount,
                "Avery Reed (TEST)",
                siteId,
                SyntheticWorkflowRole.HrIntake,
                SyntheticWorkflowRole.HrVerifier,
                SyntheticWorkflowRole.HrApprover),
            Binding(
                "person-jordan-ellis",
                HrAlternateAccount,
                "Jordan Ellis (TEST)",
                siteId,
                SyntheticWorkflowRole.HrIntake,
                SyntheticWorkflowRole.HrVerifier,
                SyntheticWorkflowRole.HrApprover),
            Binding(
                "person-morgan-lane",
                ItPrimaryAccount,
                "Morgan Lane (TEST)",
                siteId,
                SyntheticWorkflowRole.ItFormalRequester,
                SyntheticWorkflowRole.ItIssuer,
                SyntheticWorkflowRole.ItDeliverer),
            Binding(
                "person-casey-brooks",
                ItAlternateAccount,
                "Casey Brooks (TEST)",
                siteId,
                SyntheticWorkflowRole.ItFormalRequester,
                SyntheticWorkflowRole.ItIssuer,
                SyntheticWorkflowRole.ItDeliverer),
            Binding(
                "person-taylor-quinn",
                EmployeePrimaryAccount,
                "Taylor Quinn (TEST)",
                siteId),
            Binding(
                "person-riley-stone",
                EmployeeAlternateAccount,
                "Riley Stone (TEST)",
                siteId),
            Binding(
                "person-jamie-vale",
                ControlAccount,
                "Jamie Vale (TEST)",
                siteId),
            Binding(
                "person-parker-wells",
                InfrastructureAdministratorAccount,
                "Parker Wells (TEST ADMIN)",
                siteId)
        ]);
    }

    private static SyntheticIdentityBinding Binding(
        string personId,
        string accountId,
        string displayLabel,
        SyntheticSiteId siteId,
        params SyntheticWorkflowRole[] roles) =>
        new(
            new SyntheticPersonId(personId),
            new SyntheticAccountId(accountId),
            displayLabel,
            siteId,
            new HashSet<SyntheticWorkflowRole>(roles));
}
