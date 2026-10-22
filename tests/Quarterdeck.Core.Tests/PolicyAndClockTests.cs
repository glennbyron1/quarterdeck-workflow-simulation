using System.Reflection;
using Quarterdeck.Core.Simulation;

namespace Quarterdeck.Core.Tests;

public sealed class PolicyAndClockTests
{
    private const string PolicyAuthorityId = "quarterdeck-test-policy";

    private static readonly DateTimeOffset Baseline =
        new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SealBindsCurrentImmutablePolicySnapshot()
    {
        var fixture = Fixture.Create();
        var sourceOptions = new List<string> { "photo-optional" };
        fixture.Policy.Replace(Policy("policy-v2", sourceOptions));
        sourceOptions.Add("vpn-optional");

        var version = fixture.Lifecycle.Seal(
            fixture.Requester,
            fixture.Subject,
            fixture.Fields with { PolicyOptionsVersion = "policy-v2" });

        fixture.Policy.Replace(
            Policy("policy-v3", ["vpn-optional"]));

        Assert.Equal("policy-v2", version.PolicySnapshot.Version);
        Assert.Equal(["photo-optional"], version.PolicySnapshot.SelectedOptions);
        Assert.Equal("policy-v2", version.Fields.PolicyOptionsVersion);
    }

    [Fact]
    public void SealRejectsDeclaredPolicyVersionMismatchWithoutCreatingVersion()
    {
        var fixture = Fixture.Create();
        fixture.Policy.Replace(Policy("policy-v2"));

        var error = Assert.Throws<InvalidOperationException>(() =>
            fixture.Lifecycle.Seal(
                fixture.Requester,
                fixture.Subject,
                fixture.Fields));

        Assert.Equal("policy_version_mismatch", error.Message);
        Assert.Empty(fixture.Lifecycle.Versions);
    }

    [Fact]
    public void ApprovalUsesExactHalfOpenFifteenMinuteLifetime()
    {
        var fresh = Fixture.CreateApproved();
        fresh.Clock.SetUtc(Baseline.AddMinutes(14).AddSeconds(59));
        var snapshot = fresh.Lifecycle.ValidatePreDispatch(
            fresh.Issuer,
            fresh.Version!.Version,
            fresh.Approval!.ApprovalId);
        Assert.Equal(fresh.Directory.Revision, snapshot.DirectoryRevision);

        var boundary = Fixture.CreateApproved();
        var boundaryVersion = boundary.Version!;
        var boundaryApproval = boundary.Approval!;
        boundary.Clock.SetUtc(Baseline.AddMinutes(15));
        var exactExpiry = Assert.Throws<InvalidOperationException>(() =>
            boundary.Lifecycle.ValidatePreDispatch(
                boundary.Issuer,
                boundaryVersion.Version,
                boundaryApproval.ApprovalId));
        Assert.Equal("approval_expired", exactExpiry.Message);

        boundary.Clock.SetUtc(Baseline.AddMinutes(14).AddSeconds(59));
        var rollback = Assert.Throws<InvalidOperationException>(() =>
            boundary.Lifecycle.ValidatePreDispatch(
                boundary.Issuer,
                boundaryVersion.Version,
                boundaryApproval.ApprovalId));
        Assert.Equal("simulation_clock_moved_backward", rollback.Message);
    }

    [Fact]
    public void UnavailableBeforeApprovalAndBackwardClockStatesFailClosed()
    {
        var unavailable = Fixture.CreateApproved();
        unavailable.Clock.SetUnavailable();
        var unavailableError = Assert.Throws<InvalidOperationException>(() =>
            unavailable.Lifecycle.ValidatePreDispatch(
                unavailable.Issuer,
                unavailable.Version!.Version,
                unavailable.Approval!.ApprovalId));
        Assert.Equal("simulation_clock_unavailable", unavailableError.Message);

        var beforeApproval = Fixture.CreateApproved();
        beforeApproval.Clock.SetUtc(Baseline.AddSeconds(-1));
        var futureApprovalError = Assert.Throws<InvalidOperationException>(() =>
            beforeApproval.Lifecycle.ValidatePreDispatch(
                beforeApproval.Issuer,
                beforeApproval.Version!.Version,
                beforeApproval.Approval!.ApprovalId));
        Assert.Equal("simulation_clock_before_approval", futureApprovalError.Message);

        var backward = Fixture.CreateApproved();
        backward.Clock.SetUtc(Baseline.AddMinutes(5));
        _ = backward.Lifecycle.ValidatePreDispatch(
            backward.Issuer,
            backward.Version!.Version,
            backward.Approval!.ApprovalId);
        backward.Clock.SetUtc(Baseline.AddMinutes(4));
        var backwardError = Assert.Throws<InvalidOperationException>(() =>
            backward.Lifecycle.ValidatePreDispatch(
                backward.Issuer,
                backward.Version.Version,
                backward.Approval.ApprovalId));
        Assert.Equal("simulation_clock_moved_backward", backwardError.Message);
    }

    [Fact]
    public void ReapprovalCreatesNewCurrentRecordWithoutRewritingOldApproval()
    {
        var fixture = Fixture.CreateApproved();
        var version = fixture.Version!;
        var first = fixture.Approval!;
        fixture.Clock.SetUtc(Baseline.AddMinutes(15));
        var expired = Assert.Throws<InvalidOperationException>(() =>
            fixture.Lifecycle.ValidatePreDispatch(
                fixture.Issuer,
                version.Version,
                first.ApprovalId));
        Assert.Equal("approval_expired", expired.Message);

        var second = fixture.Lifecycle.Approve(
            fixture.Hr,
            fixture.Subject,
            version.Version,
            fixture.Fields);

        Assert.NotEqual(first.ApprovalId, second.ApprovalId);
        Assert.Equal(Baseline, first.ApprovedAt);
        Assert.Equal(Baseline.AddMinutes(15), first.ExpiresAt);
        Assert.False(first.Current);
        Assert.True(first.Valid);
        Assert.True(second.Current);
        Assert.Equal(Baseline.AddMinutes(15), second.ApprovedAt);
        Assert.Equal(Baseline.AddMinutes(30), second.ExpiresAt);

        var oldError = Assert.Throws<InvalidOperationException>(() =>
            fixture.Lifecycle.ValidatePreDispatch(
                fixture.Issuer,
                version.Version,
                first.ApprovalId));
        Assert.Equal("approval_not_current", oldError.Message);
        _ = fixture.Lifecycle.ValidatePreDispatch(
            fixture.Issuer,
            version.Version,
            second.ApprovalId);
    }

    [Fact]
    public void PolicyMigrationIsAttributedAndRequiresFreshVersionApproval()
    {
        var fixture = Fixture.CreateApproved();
        var firstVersion = fixture.Version!;
        var firstApproval = fixture.Approval!;
        fixture.Policy.Replace(
            Policy(
                "policy-v2",
                ["photo-optional", "vpn-optional"]));
        fixture.Clock.SetUtc(Baseline.AddMinutes(1));

        var migrated = fixture.Lifecycle.MigratePolicy(
            fixture.Requester,
            fixture.Subject,
            "global-policy-v2");

        Assert.True(firstVersion.Superseded);
        Assert.False(firstApproval.Valid);
        Assert.Equal("policy-v2", migrated.Fields.PolicyOptionsVersion);
        Assert.Equal("policy-v2", migrated.PolicySnapshot.Version);
        Assert.Equal(2L, migrated.Version);
        var attribution = Assert.IsType<PolicyMigrationAttribution>(migrated.PolicyMigration);
        Assert.Equal(fixture.Requester.PersonId, attribution.ActorPersonId);
        Assert.Equal(fixture.Requester.AccountId, attribution.ActorAccountId);
        Assert.Equal(SyntheticWorkflowRole.ItFormalRequester, attribution.ActorRole);
        Assert.Equal("global-policy-v2", attribution.Reason);
        Assert.Equal(Baseline.AddMinutes(1), attribution.OccurredAt);
        Assert.Equal(firstVersion.Version, attribution.SourceFormalVersion);
        Assert.Equal(migrated.Version, attribution.ReplacementFormalVersion);
        Assert.Same(migrated.PolicySnapshot, attribution.ReplacementPolicy);

        var missingVerification = Assert.Throws<InvalidOperationException>(() =>
            fixture.Lifecycle.Approve(
                fixture.Hr,
                fixture.Subject,
                migrated.Version,
                migrated.Fields));
        Assert.Equal("identity_verification_required", missingVerification.Message);

        fixture.Lifecycle.VerifyIdentity(fixture.Hr, migrated.Version);
        var migratedApproval = fixture.Lifecycle.Approve(
            fixture.Hr,
            fixture.Subject,
            migrated.Version,
            migrated.Fields);
        Assert.Equal(migrated.Version, migratedApproval.FormalVersion);
        Assert.NotEqual(firstApproval.ApprovalId, migratedApproval.ApprovalId);
    }

    [Fact]
    public void SamePolicyVersionCannotBeRedefinedWithDifferentOptions()
    {
        var fixture = Fixture.Create();
        _ = fixture.Lifecycle.Seal(
            fixture.Requester,
            fixture.Subject,
            fixture.Fields);

        var error = Assert.Throws<InvalidOperationException>(() =>
            fixture.Policy.Replace(
                Policy("policy-v1", ["different-option"])));

        Assert.Equal("policy_version_mismatch", error.Message);
        Assert.Single(fixture.Lifecycle.Versions);
        Assert.False(fixture.Lifecycle.Versions[0].Superseded);
        Assert.Equal(
            ["physical-piv-only"],
            fixture.Policy.CaptureCurrent().SelectedOptions);
    }

    [Fact]
    public void PolicyIdentityIncludesAuthorityVersionAndCanonicalContentDigest()
    {
        var first = SimulationPolicySnapshot.Create(
            "policy-authority-a",
            "policy-v1",
            ["vpn-optional", "photo-optional", "vpn-optional"]);
        var reordered = SimulationPolicySnapshot.Create(
            "policy-authority-a",
            "policy-v1",
            ["photo-optional", "vpn-optional"]);
        var differentContent = SimulationPolicySnapshot.Create(
            "policy-authority-a",
            "policy-v1",
            ["physical-piv-only"]);
        var differentAuthority = SimulationPolicySnapshot.Create(
            "policy-authority-b",
            "policy-v1",
            ["photo-optional", "vpn-optional"]);

        Assert.Equal(first.ContentDigest, reordered.ContentDigest);
        Assert.Equal(first.Identity, reordered.Identity);
        Assert.Equal(64, first.ContentDigest.Length);
        Assert.Matches("^[0-9a-f]{64}$", first.ContentDigest);
        Assert.NotEqual(first.ContentDigest, differentContent.ContentDigest);
        Assert.NotEqual(first.Identity, differentContent.Identity);
        Assert.Equal(first.ContentDigest, differentAuthority.ContentDigest);
        Assert.NotEqual(first.Identity, differentAuthority.Identity);

        var source = new SyntheticSimulationPolicySource(first);
        var independentSource = new SyntheticSimulationPolicySource(differentContent);
        Assert.NotEqual(
            source.CaptureCurrent().Identity,
            independentSource.CaptureCurrent().Identity);
        var authorityError = Assert.Throws<InvalidOperationException>(() =>
            source.Replace(differentAuthority));
        Assert.Equal("policy_authority_mismatch", authorityError.Message);
        Assert.Equal(first.Identity, source.CaptureCurrent().Identity);
    }

    [Fact]
    public void PolicyContentDigestBindsEachOptionClassification()
    {
        var required = SimulationPolicySnapshot.CreateClassified(
            "policy-authority",
            "policy-v1",
            [new("vpn-access", SimulationPolicyOptionClassification.Required)]);
        var optional = SimulationPolicySnapshot.CreateClassified(
            "policy-authority",
            "policy-v1",
            [new("vpn-access", SimulationPolicyOptionClassification.OptionalSelected)]);
        var notSelected = SimulationPolicySnapshot.CreateClassified(
            "policy-authority",
            "policy-v1",
            [new("vpn-access", SimulationPolicyOptionClassification.NotSelected)]);

        Assert.NotEqual(required.ContentDigest, optional.ContentDigest);
        Assert.NotEqual(optional.ContentDigest, notSelected.ContentDigest);
        Assert.Equal(["vpn-access"], required.SelectedOptions);
        Assert.Equal(["vpn-access"], optional.SelectedOptions);
        Assert.Empty(notSelected.SelectedOptions);
        Assert.StartsWith(
            "policy_option_classification_conflict",
            Assert.Throws<ArgumentException>(() => SimulationPolicySnapshot.CreateClassified(
                "policy-authority",
                "policy-v1",
                [
                    new("vpn-access", SimulationPolicyOptionClassification.Required),
                    new("vpn-access", SimulationPolicyOptionClassification.OptionalSelected)
                ])).Message);
        Assert.StartsWith(
            "policy_option_classification_invalid",
            Assert.Throws<ArgumentException>(() => SimulationPolicySnapshot.CreateClassified(
                "policy-authority",
                "policy-v1",
                [new("vpn-access", (SimulationPolicyOptionClassification)99)])).Message);
    }

    [Fact]
    public void LifecycleRejectsAuthorityChangeFromCustomPolicySource()
    {
        var source = new MutablePolicySource(Policy("policy-v1", ["physical-piv-only"]));
        var fixture = Fixture.Create(policySource: source);
        var version = fixture.Lifecycle.Seal(
            fixture.Requester,
            fixture.Subject,
            fixture.Fields);
        fixture.Lifecycle.VerifyIdentity(fixture.Hr, version.Version);
        fixture.ConfigureApprovalReadiness();
        var approval = fixture.Lifecycle.Approve(
            fixture.Hr,
            fixture.Subject,
            version.Version,
            fixture.Fields);
        source.Current = SimulationPolicySnapshot.Create(
            "replacement-policy-authority",
            "policy-v2",
            ["vpn-optional"]);
        fixture.Clock.SetUtc(Baseline.AddMinutes(1));

        var error = Assert.Throws<InvalidOperationException>(() =>
            fixture.Lifecycle.MigratePolicy(
                fixture.Requester,
                fixture.Subject,
                "policy-v2"));

        Assert.Equal("policy_authority_mismatch", error.Message);
        Assert.Single(fixture.Lifecycle.Versions);
        Assert.Same(version, fixture.Lifecycle.Versions[0]);
        Assert.False(version.Superseded);
        Assert.True(approval.Valid);
        Assert.True(approval.Current);
    }

    [Fact]
    public void SealCannotBypassAttributedPolicyMigration()
    {
        var fixture = Fixture.CreateApproved();
        fixture.Policy.Replace(Policy("policy-v2"));

        var error = Assert.Throws<InvalidOperationException>(() =>
            fixture.Lifecycle.Seal(
                fixture.Requester,
                fixture.Subject,
                fixture.Fields with { PolicyOptionsVersion = "policy-v2" }));

        Assert.Equal("policy_migration_required", error.Message);
        Assert.Single(fixture.Lifecycle.Versions);
        Assert.False(fixture.Version!.Superseded);
        Assert.True(fixture.Approval!.Valid);
    }

    [Fact]
    public void ApprovalRejectsClockValueThatCannotRepresentFullLifetime()
    {
        var fixture = Fixture.Create();
        var version = fixture.Lifecycle.Seal(
            fixture.Requester,
            fixture.Subject,
            fixture.Fields);
        fixture.Lifecycle.VerifyIdentity(fixture.Hr, version.Version);
        fixture.ConfigureApprovalReadiness();
        fixture.Clock.SetUtc(DateTimeOffset.MaxValue);

        var error = Assert.Throws<InvalidOperationException>(() =>
            fixture.Lifecycle.Approve(
                fixture.Hr,
                fixture.Subject,
                version.Version,
                fixture.Fields));

        Assert.Equal("simulation_clock_unavailable", error.Message);
        Assert.Empty(fixture.Lifecycle.Approvals);

        fixture.Clock.SetUtc(Baseline);
        var recovered = fixture.Lifecycle.Approve(
            fixture.Hr,
            fixture.Subject,
            version.Version,
            fixture.Fields);
        Assert.Equal(Baseline, recovered.ApprovedAt);
    }

    [Fact]
    public void MigrationRejectsUnrepresentableApprovalLifetimeWithoutMutation()
    {
        var fixture = Fixture.CreateApproved();
        var version = fixture.Version!;
        var approval = fixture.Approval!;
        fixture.Policy.Replace(Policy("policy-v2", ["vpn-optional"]));
        fixture.Clock.SetUtc(DateTimeOffset.MaxValue);

        var error = Assert.Throws<InvalidOperationException>(() =>
            fixture.Lifecycle.MigratePolicy(
                fixture.Requester,
                fixture.Subject,
                "policy-v2"));

        Assert.Equal("simulation_clock_unavailable", error.Message);
        Assert.Single(fixture.Lifecycle.Versions);
        Assert.Same(version, fixture.Lifecycle.Versions[0]);
        Assert.False(version.Superseded);
        Assert.True(approval.Valid);
        Assert.True(approval.Current);

        fixture.Clock.SetUtc(Baseline.AddMinutes(1));
        var migrated = fixture.Lifecycle.MigratePolicy(
            fixture.Requester,
            fixture.Subject,
            "policy-v2");
        Assert.Equal(2L, migrated.Version);
        Assert.True(version.Superseded);
        Assert.False(approval.Valid);
    }

    [Fact]
    public void MigrationAtLatestRepresentableApprovalTimeAllowsFreshApproval()
    {
        var fixture = Fixture.CreateApproved();
        fixture.Policy.Replace(Policy("policy-v2", ["vpn-optional"]));
        var latest = DateTimeOffset.MaxValue.Subtract(TimeSpan.FromMinutes(15));
        fixture.Clock.SetUtc(latest);

        var migrated = fixture.Lifecycle.MigratePolicy(
            fixture.Requester,
            fixture.Subject,
            "policy-v2");
        fixture.Lifecycle.VerifyIdentity(fixture.Hr, migrated.Version);
        fixture.ConfigureApprovalReadiness();
        var approval = fixture.Lifecycle.Approve(
            fixture.Hr,
            fixture.Subject,
            migrated.Version,
            migrated.Fields);

        Assert.Equal(latest, approval.ApprovedAt);
        Assert.Equal(DateTimeOffset.MaxValue, approval.ExpiresAt);
    }

    [Fact]
    public void RejectedApprovalReadinessDoesNotAdvanceClockWatermark()
    {
        var fixture = Fixture.Create();
        var version = fixture.Lifecycle.Seal(
            fixture.Requester,
            fixture.Subject,
            fixture.Fields);
        fixture.Lifecycle.VerifyIdentity(fixture.Hr, version.Version);
        fixture.Clock.SetUtc(Baseline.AddMinutes(10));

        var rejected = Assert.Throws<InvalidOperationException>(() =>
            fixture.Lifecycle.Approve(
                fixture.Hr,
                fixture.Subject,
                version.Version,
                fixture.Fields));
        Assert.Equal("containment_state_unknown", rejected.Message);
        Assert.Empty(fixture.Lifecycle.Approvals);

        fixture.ConfigureApprovalReadiness();
        fixture.Clock.SetUtc(Baseline);
        var approval = fixture.Lifecycle.Approve(
            fixture.Hr,
            fixture.Subject,
            version.Version,
            fixture.Fields);

        Assert.Equal(Baseline, approval.ApprovedAt);
        Assert.Same(version.PolicySnapshot, approval.ApprovedPolicySnapshot);
    }

    [Fact]
    public void RejectedPreDispatchReadinessDoesNotAdvanceClockWatermark()
    {
        var fixture = Fixture.CreateApproved();
        fixture.SetContainmentState(SyntheticContainmentState.Contained);
        fixture.Clock.SetUtc(Baseline.AddMinutes(5));

        var rejected = Assert.Throws<InvalidOperationException>(() =>
            fixture.Lifecycle.ValidatePreDispatch(
                fixture.Issuer,
                fixture.Version!.Version,
                fixture.Approval!.ApprovalId));
        Assert.Equal("subject_contained", rejected.Message);

        fixture.SetContainmentState(SyntheticContainmentState.Clear);
        fixture.Clock.SetUtc(Baseline.AddMinutes(1));
        var readiness = fixture.Lifecycle.ValidatePreDispatch(
            fixture.Issuer,
            fixture.Version!.Version,
            fixture.Approval!.ApprovalId);

        Assert.Equal(fixture.Directory.Revision, readiness.DirectoryRevision);
    }

    [Fact]
    public void DependencyCallbacksCannotReenterLifecycleTransitions()
    {
        var callbackClock = new OneShotCallbackClock(Baseline);
        var fixture = Fixture.Create(approvalClock: callbackClock);
        var version = fixture.Lifecycle.Seal(
            fixture.Requester,
            fixture.Subject,
            fixture.Fields);
        fixture.Lifecycle.VerifyIdentity(fixture.Hr, version.Version);
        fixture.ConfigureApprovalReadiness();

        callbackClock.SetCallback(() => fixture.Lifecycle.Seal(
            fixture.Requester,
            fixture.Subject,
            fixture.Fields));
        var approval = fixture.Lifecycle.Approve(
            fixture.Hr,
            fixture.Subject,
            version.Version,
            fixture.Fields);

        Assert.Equal("simulation_dependency_reentry", callbackClock.LastError?.Message);
        Assert.Single(fixture.Lifecycle.Versions);
        Assert.False(version.Superseded);
        Assert.True(approval.Valid);
        Assert.True(approval.Current);

        callbackClock.SetCallback(() => fixture.Lifecycle.Seal(
            fixture.Requester,
            fixture.Subject,
            fixture.Fields));
        _ = fixture.Lifecycle.ValidatePreDispatch(
            fixture.Issuer,
            version.Version,
            approval.ApprovalId);

        Assert.Equal("simulation_dependency_reentry", callbackClock.LastError?.Message);
        Assert.Single(fixture.Lifecycle.Versions);
        Assert.False(version.Superseded);
        Assert.True(approval.Valid);
        Assert.True(approval.Current);
    }

    [Fact]
    public void PolicyCallbackCannotCreateNestedVersion()
    {
        var callbackPolicy = new OneShotCallbackPolicySource(
            Policy("policy-v1", ["physical-piv-only"]));
        var fixture = Fixture.Create(policySource: callbackPolicy);
        callbackPolicy.SetCallback(() => fixture.Lifecycle.Seal(
            fixture.Requester,
            fixture.Subject,
            fixture.Fields));

        var version = fixture.Lifecycle.Seal(
            fixture.Requester,
            fixture.Subject,
            fixture.Fields);

        Assert.Equal("simulation_dependency_reentry", callbackPolicy.LastError?.Message);
        Assert.Single(fixture.Lifecycle.Versions);
        Assert.Same(version, fixture.Lifecycle.Versions[0]);
        Assert.False(version.Superseded);
    }

    [Fact]
    public void ApprovalIdsDoNotCollideAcrossLifecyclesWithSameCaseId()
    {
        var first = Fixture.CreateApproved();
        var second = Fixture.CreateApproved();

        Assert.Equal(first.Lifecycle.Intake.CaseId, second.Lifecycle.Intake.CaseId);
        Assert.NotEqual(first.Approval!.ApprovalId, second.Approval!.ApprovalId);
    }

    private sealed class OneShotCallbackClock(DateTimeOffset utcNow)
        : ISimulationApprovalClock
    {
        private Action? _callback;

        public InvalidOperationException? LastError { get; private set; }

        public void SetCallback(Action callback)
        {
            _callback = callback;
            LastError = null;
        }

        public SimulationClockObservation Observe()
        {
            var callback = Interlocked.Exchange(ref _callback, null);
            if (callback is not null)
            {
                try
                {
                    callback();
                }
                catch (InvalidOperationException error)
                {
                    LastError = error;
                }
            }

            return SimulationClockObservation.At(utcNow);
        }
    }

    private sealed class OneShotCallbackPolicySource(SimulationPolicySnapshot snapshot)
        : ISimulationPolicySource
    {
        private Action? _callback;

        public InvalidOperationException? LastError { get; private set; }

        public void SetCallback(Action callback)
        {
            _callback = callback;
            LastError = null;
        }

        public SimulationPolicySnapshot CaptureCurrent()
        {
            var callback = Interlocked.Exchange(ref _callback, null);
            if (callback is not null)
            {
                try
                {
                    callback();
                }
                catch (InvalidOperationException error)
                {
                    LastError = error;
                }
            }

            return snapshot;
        }
    }

    private sealed class MutablePolicySource(SimulationPolicySnapshot current)
        : ISimulationPolicySource
    {
        public SimulationPolicySnapshot Current { get; set; } = current;

        public SimulationPolicySnapshot CaptureCurrent() => Current;
    }

    private sealed class Fixture
    {
        private Fixture(
            SyntheticIdentityDirectory directory,
            SyntheticActor subject,
            SyntheticActor hr,
            SyntheticActor requester,
            SyntheticActor issuer,
            FormalRequestFields fields,
            SyntheticSimulationPolicySource policy,
            SyntheticSimulationApprovalClock clock,
            FormalRequestLifecycle lifecycle)
        {
            Directory = directory;
            Subject = subject;
            Hr = hr;
            Requester = requester;
            Issuer = issuer;
            Fields = fields;
            Policy = policy;
            Clock = clock;
            Lifecycle = lifecycle;
        }

        public SyntheticIdentityDirectory Directory { get; }
        public SyntheticActor Subject { get; }
        public SyntheticActor Hr { get; }
        public SyntheticActor Requester { get; }
        public SyntheticActor Issuer { get; }
        public FormalRequestFields Fields { get; }
        public SyntheticSimulationPolicySource Policy { get; }
        public SyntheticSimulationApprovalClock Clock { get; }
        public FormalRequestLifecycle Lifecycle { get; }
        public FormalRequestVersion? Version { get; private set; }
        public ExactVersionApproval? Approval { get; private set; }

        public static Fixture Create(
            ISimulationPolicySource? policySource = null,
            ISimulationApprovalClock? approvalClock = null)
        {
            var site = new SyntheticSiteId("clock-site");
            var person = new SyntheticPersonId("clock-subject-person");
            var subjectAccount = new SyntheticAccountId("clock-subject-account");
            var hrAccount = new SyntheticAccountId("clock-hr-account");
            var requesterAccount = new SyntheticAccountId("clock-requester-account");
            var issuerAccount = new SyntheticAccountId("clock-issuer-account");
            var directory = SyntheticIdentityDirectory.Create(
            [
                new(person, subjectAccount, "Clock Subject", site, Roles()),
                new(
                    new("clock-hr-person"),
                    hrAccount,
                    "Clock HR",
                    site,
                    Roles(
                        SyntheticWorkflowRole.HrIntake,
                        SyntheticWorkflowRole.HrVerifier,
                        SyntheticWorkflowRole.HrApprover)),
                new(
                    new("clock-requester-person"),
                    requesterAccount,
                    "Clock Requester",
                    site,
                    Roles(SyntheticWorkflowRole.ItFormalRequester)),
                new(
                    new("clock-issuer-person"),
                    issuerAccount,
                    "Clock Issuer",
                    site,
                    Roles(SyntheticWorkflowRole.ItIssuer))
            ]);
            var policy = new SyntheticSimulationPolicySource(
                Policy("policy-v1", ["physical-piv-only"]));
            var clock = new SyntheticSimulationApprovalClock(Baseline);
            var intake = HrIntake.Create(
                "clock-case",
                directory.Resolve(hrAccount),
                directory,
                person,
                site,
                [subjectAccount]);
            var fields = new FormalRequestFields(
                site,
                person,
                subjectAccount,
                FormalRequestFields.RequiredIntendedUse,
                "clock-fake-device",
                FormalRequestFields.RequiredSlot,
                "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
                "clock-fake-authority",
                "clock-template",
                "policy-v1");
            return new Fixture(
                directory,
                directory.Resolve(subjectAccount),
                directory.Resolve(hrAccount),
                directory.Resolve(requesterAccount),
                directory.Resolve(issuerAccount),
                fields,
                policy,
                clock,
                new FormalRequestLifecycle(
                    intake,
                    policySource ?? policy,
                    approvalClock ?? clock));
        }

        public static Fixture CreateApproved()
        {
            var fixture = Create();
            fixture.Version = fixture.Lifecycle.Seal(
                fixture.Requester,
                fixture.Subject,
                fixture.Fields);
            fixture.Lifecycle.VerifyIdentity(fixture.Hr, fixture.Version.Version);
            fixture.ConfigureApprovalReadiness();
            fixture.Approval = fixture.Lifecycle.Approve(
                fixture.Hr,
                fixture.Subject,
                fixture.Version.Version,
                fixture.Fields);
            return fixture;
        }

        public void ConfigureApprovalReadiness()
        {
            SetFixtureState(
                Directory,
                "SetContainmentState",
                Fields.AccountId,
                SyntheticContainmentState.Clear);
            SetFixtureState(
                Directory,
                "SetDeviceQualificationState",
                Fields.FakeDeviceId,
                SyntheticDeviceQualificationState.Qualified);
            SetFixtureState(
                Directory,
                "SetAuthorityAvailabilityState",
                Fields.FakeAuthority,
                SyntheticAuthorityAvailabilityState.Available);
        }

        public void SetContainmentState(SyntheticContainmentState state) =>
            SetFixtureState(
                Directory,
                "SetContainmentState",
                Fields.AccountId,
                state);

        private static HashSet<SyntheticWorkflowRole> Roles(
            params SyntheticWorkflowRole[] roles) => [.. roles];

        private static void SetFixtureState(
            SyntheticIdentityDirectory directory,
            string methodName,
            params object[] arguments)
        {
            var method = typeof(SyntheticIdentityDirectory).GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"Missing fixture method: {methodName}");
            _ = Assert.IsType<long>(method.Invoke(directory, arguments));
        }
    }

    private static SimulationPolicySnapshot Policy(
        string version,
        IEnumerable<string>? selectedOptions = null) =>
        SimulationPolicySnapshot.Create(
            PolicyAuthorityId,
            version,
            selectedOptions);
}
