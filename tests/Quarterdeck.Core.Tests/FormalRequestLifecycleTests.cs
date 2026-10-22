using System.Reflection;
using Quarterdeck.Core.Simulation;

namespace Quarterdeck.Core.Tests;

public sealed class FormalRequestLifecycleTests
{
    [Fact]
    public void FixtureRegistrationRollsBackWhenDurableResolutionFails()
    {
        var site = new SyntheticSiteId("SITE-TEST");
        var directory = SyntheticIdentityDirectory.Create([
            new(new("TEST-IT"), new("TEST-IT-ACCOUNT"), "Test IT", site,
                new HashSet<SyntheticWorkflowRole> { SyntheticWorkflowRole.ItFormalRequester })]);
        var actor = directory.Resolve(new("TEST-IT-ACCOUNT"));
        Assert.Throws<IOException>(() => directory.RegisterSimulationSubjectAccount<int>(
            actor, new("TEST-NEW"), site, new("TEST-NEW-ACCOUNT"), "Test New",
            () => throw new IOException("simulated durable write failure")));
        Assert.Equal(1, directory.AccountCount);
        var result = directory.RegisterSimulationSubjectAccount(
            actor, new("TEST-NEW"), site, new("TEST-NEW-ACCOUNT"), "Test New", () => 1);
        Assert.Equal(1, result);
        Assert.Equal(2, directory.AccountCount);
    }

    private const string PublicRequestDigest =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("case/escape")]
    [InlineData("case escape")]
    [InlineData("case.control\n")]
    [InlineData(".hidden")]
    public void IntakeRejectsPathLikeOrNonTokenCaseIds(string caseId)
    {
        var fixture = LifecycleFixture.Create();

        var error = Assert.Throws<ArgumentException>(() => HrIntake.Create(
            caseId,
            fixture.Hr,
            fixture.Directory,
            fixture.SubjectPersonId,
            fixture.SiteId,
            [fixture.SubjectAccountId]));

        Assert.Equal("caseId", error.ParamName);
        Assert.StartsWith("case_id_invalid", error.Message);
    }

    [Fact]
    public void IntakeAcceptsEightyCharacterCaseIdAndRejectsEightyOne()
    {
        var fixture = LifecycleFixture.Create();
        var accepted = new string('a', 80);

        var intake = HrIntake.Create(
            accepted,
            fixture.Hr,
            fixture.Directory,
            fixture.SubjectPersonId,
            fixture.SiteId,
            [fixture.SubjectAccountId]);

        Assert.Equal(accepted, intake.CaseId);
        var error = Assert.Throws<ArgumentException>(() => HrIntake.Create(
            new string('a', 81),
            fixture.Hr,
            fixture.Directory,
            fixture.SubjectPersonId,
            fixture.SiteId,
            [fixture.SubjectAccountId]));
        Assert.StartsWith("case_id_invalid", error.Message);
    }

    [Fact]
    public void IntakeCapturesDurableActorAccountAndDirectoryRevision()
    {
        var fixture = LifecycleFixture.Create();
        var expectedRevision = fixture.Directory.Revision;

        var intake = HrIntake.Create(
            "case-authority-snapshot",
            fixture.Hr,
            fixture.Directory,
            fixture.SubjectPersonId,
            fixture.SiteId,
            [fixture.SubjectAccountId]);

        Assert.Equal(fixture.Hr.PersonId, intake.IntakeActorPersonId);
        Assert.Equal(fixture.Hr.AccountId, intake.IntakeActorAccountId);
        Assert.Equal(fixture.Hr.DisplayLabel, intake.IntakeActorDisplayLabel);
        Assert.Equal(expectedRevision, intake.DirectoryRevision);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789")]
    [InlineData("g123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789abcdef")]
    public void SealRejectsNonCanonicalPublicRequestDigest(string digest)
    {
        var fixture = LifecycleFixture.Create();
        var lifecycle = fixture.CreateReadyLifecycle();

        var error = Assert.Throws<ArgumentException>(() => lifecycle.Seal(
            fixture.Requester,
            fixture.Subject,
            fixture.Fields with { SyntheticPublicRequestDigest = digest }));

        Assert.Equal("public_request_digest_invalid", error.Message);
        Assert.Empty(lifecycle.Versions);
    }

    [Fact]
    public void WaitingIntakeCanBeResolvedToExactExistingAccountOnSameCase()
    {
        var fixture = LifecycleFixture.Create();
        var submittedCandidate = new SyntheticAccountId("submitted-missing-account");
        var intake = HrIntake.Create(
            "case-waiting",
            fixture.Hr,
            fixture.Directory,
            fixture.SubjectPersonId,
            fixture.SiteId,
            [submittedCandidate]);
        var lifecycle = fixture.CreateLifecycle(intake);

        Assert.Equal(IntakeState.WaitingForIt, lifecycle.CurrentIntakeState);
        Assert.Equal(HrIntake.WaitingNextAction, lifecycle.RequiredNextAction);
        Assert.Equal(submittedCandidate, Assert.Single(intake.CandidateAccounts));

        var missingReason = Assert.Throws<InvalidOperationException>(() =>
            lifecycle.ResolveWaitingForIt(
                fixture.Requester,
                fixture.SubjectAccountId));
        Assert.Equal("account_correction_reason_required", missingReason.Message);
        Assert.Empty(lifecycle.AccountResolutions);

        var unsafeReason = Assert.Throws<ArgumentException>(() =>
            lifecycle.ResolveWaitingForIt(
                fixture.Requester,
                fixture.SubjectAccountId,
                "free form reason"));
        Assert.StartsWith("account_correction_reason_invalid", unsafeReason.Message);
        Assert.Equal("correctionReason", unsafeReason.ParamName);
        Assert.Empty(lifecycle.AccountResolutions);

        var resolution = lifecycle.ResolveWaitingForIt(
            fixture.Requester,
            fixture.SubjectAccountId,
            "hr-candidate-unresolved");

        Assert.Equal("case-waiting", resolution.CaseId);
        Assert.Equal(fixture.SubjectAccountId, resolution.AccountId);
        Assert.Equal(fixture.Requester.PersonId, resolution.ResolverPersonId);
        Assert.Equal(fixture.Requester.AccountId, resolution.ResolverAccountId);
        Assert.Equal(fixture.Directory.Revision, resolution.DirectoryRevision);
        Assert.False(resolution.SubmittedCandidateMatched);
        Assert.Equal("hr-candidate-unresolved", resolution.CorrectionReason);
        Assert.Equal(submittedCandidate, Assert.Single(intake.CandidateAccounts));
        Assert.Equal(IntakeState.ReadyForItValidation, lifecycle.CurrentIntakeState);
        Assert.Empty(lifecycle.RequiredNextAction);

        var version = lifecycle.Seal(
            fixture.Requester,
            fixture.Subject,
            fixture.Fields);
        Assert.Equal(1L, version.Version);
        Assert.Single(lifecycle.AccountResolutions);
    }

    [Fact]
    public void WaitingIntakeResolutionRequiresCurrentItRequesterRole()
    {
        var fixture = LifecycleFixture.Create();
        var intake = HrIntake.Create(
            "case-no-it-role",
            fixture.Hr,
            fixture.Directory,
            fixture.SubjectPersonId,
            fixture.SiteId,
            []);
        var lifecycle = fixture.CreateLifecycle(intake);

        var error = Assert.Throws<InvalidOperationException>(() =>
            lifecycle.ResolveWaitingForIt(
                fixture.Hr,
                fixture.SubjectAccountId,
                "hr-candidate-missing"));

        Assert.Equal("required_role_missing", error.Message);
        Assert.Equal(IntakeState.WaitingForIt, lifecycle.CurrentIntakeState);
        Assert.Empty(lifecycle.AccountResolutions);
    }

    [Fact]
    public void ApprovalRequiresSeparateVerificationAndExactBoundFields()
    {
        var fixture = LifecycleFixture.Create();
        var lifecycle = fixture.CreateReadyLifecycle();
        var version = lifecycle.Seal(
            fixture.Requester,
            fixture.Subject,
            fixture.Fields);
        var sealedDirectoryRevision = fixture.Directory.Revision;

        var missingVerification = Assert.Throws<InvalidOperationException>(() =>
            lifecycle.Approve(
                fixture.Hr,
                fixture.Subject,
                version.Version,
                fixture.Fields));
        Assert.Equal("identity_verification_required", missingVerification.Message);

        var attestation = lifecycle.VerifyIdentity(fixture.Hr, version.Version);
        var notReady = Assert.Throws<InvalidOperationException>(() =>
            lifecycle.Approve(
                fixture.Hr,
                fixture.Subject,
                version.Version,
                fixture.Fields));
        Assert.Equal("containment_state_unknown", notReady.Message);
        Assert.Empty(lifecycle.Approvals);

        fixture.ConfigureApprovalReadiness();
        var approval = lifecycle.Approve(
            fixture.Hr,
            fixture.Subject,
            version.Version,
            fixture.Fields);

        Assert.Equal(version.Version, attestation.FormalVersion);
        Assert.Equal(version.Version, approval.FormalVersion);
        Assert.True(Guid.TryParseExact(approval.ApprovalId, "D", out _));
        Assert.Equal(36, approval.ApprovalId.Length);
        Assert.Equal(fixture.Directory.Revision, approval.DirectoryRevision);
        Assert.Equal(version.SealedRequestDigest, approval.ApprovedRequestDigest);
        Assert.Equal(sealedDirectoryRevision, version.OperationBindingInputRevision);
        Assert.True(version.OperationBindingInputRevision < approval.DirectoryRevision);
        Assert.Matches("^sha256:[0-9a-f]{64}$", version.SealedRequestDigest);
        Assert.True(approval.Valid);

        var changedFields = fixture.Fields with { Template = "template-changed" };
        var changed = Assert.Throws<InvalidOperationException>(() =>
            lifecycle.Approve(
                fixture.Hr,
                fixture.Subject,
                version.Version,
                changedFields));

        Assert.Equal("bound_fields_changed", changed.Message);
        Assert.True(approval.Valid);
        Assert.Single(lifecycle.Approvals);
    }

    [Fact]
    public void RequesterAliasCannotApproveForSamePerson()
    {
        var fixture = LifecycleFixture.Create();
        var lifecycle = fixture.CreateReadyLifecycle();
        var version = lifecycle.Seal(
            fixture.Requester,
            fixture.Subject,
            fixture.Fields);
        lifecycle.VerifyIdentity(fixture.Hr, version.Version);
        fixture.ConfigureApprovalReadiness();

        var error = Assert.Throws<InvalidOperationException>(() =>
            lifecycle.Approve(
                fixture.RequesterApproverAlias,
                fixture.Subject,
                version.Version,
                fixture.Fields));

        Assert.Equal("requester_approver_same_person", error.Message);
        Assert.Empty(lifecycle.Approvals);
    }

    [Fact]
    public void NewFormalVersionInvalidatesApprovalAndRetainedReferences()
    {
        var fixture = LifecycleFixture.Create();
        var lifecycle = fixture.CreateReadyLifecycle();
        var first = lifecycle.Seal(
            fixture.Requester,
            fixture.Subject,
            fixture.Fields);
        lifecycle.VerifyIdentity(fixture.Hr, first.Version);
        fixture.ConfigureApprovalReadiness();
        var approval = lifecycle.Approve(
            fixture.Hr,
            fixture.Subject,
            first.Version,
            fixture.Fields);

        var secondFields = fixture.Fields with { Template = "template-v2" };
        var second = lifecycle.Seal(
            fixture.Requester,
            fixture.Subject,
            secondFields);

        Assert.Equal(2L, second.Version);
        Assert.True(first.Superseded);
        Assert.False(approval.Valid);
        Assert.True(lifecycle.Versions[0].Superseded);
        Assert.False(lifecycle.Approvals[0].Valid);

        var error = Assert.Throws<InvalidOperationException>(() =>
            lifecycle.VerifyIdentity(fixture.Hr, first.Version));
        Assert.Equal("formal_version_superseded", error.Message);
    }

    [Fact]
    public void PreDispatchReadinessRechecksContainmentAfterApproval()
    {
        var fixture = LifecycleFixture.Create();
        var lifecycle = fixture.CreateReadyLifecycle();
        var version = lifecycle.Seal(
            fixture.Requester,
            fixture.Subject,
            fixture.Fields);
        lifecycle.VerifyIdentity(fixture.Hr, version.Version);
        fixture.ConfigureApprovalReadiness();
        var approval = lifecycle.Approve(
            fixture.Hr,
            fixture.Subject,
            version.Version,
            fixture.Fields);
        fixture.SetContainmentState(SyntheticContainmentState.Unknown);

        var error = Assert.Throws<InvalidOperationException>(() =>
            lifecycle.ValidatePreDispatch(
                fixture.Issuer,
                version.Version,
                approval.ApprovalId));

        Assert.Equal("containment_state_unknown", error.Message);
    }

    [Fact]
    public void DirectorySnapshotsCallerOwnedRolesAndRechecksDisabledActors()
    {
        var roles = new HashSet<SyntheticWorkflowRole>
        {
            SyntheticWorkflowRole.HrIntake
        };
        var siteId = new SyntheticSiteId("snapshot-site");
        var subjectPersonId = new SyntheticPersonId("snapshot-subject-person");
        var subjectAccountId = new SyntheticAccountId("snapshot-subject-account");
        var hrAccountId = new SyntheticAccountId("snapshot-hr-account");
        var disabledHrAccountId = new SyntheticAccountId("snapshot-disabled-hr-account");
        var directory = SyntheticIdentityDirectory.Create(
        [
            new(
                subjectPersonId,
                subjectAccountId,
                "Snapshot Subject",
                siteId,
                new HashSet<SyntheticWorkflowRole>()),
            new(new("snapshot-hr-person"), hrAccountId, "Snapshot HR", siteId, roles),
            new(
                new("snapshot-disabled-hr-person"),
                disabledHrAccountId,
                "Disabled HR",
                siteId,
                new HashSet<SyntheticWorkflowRole> { SyntheticWorkflowRole.HrIntake },
                AccountEnabled: false)
        ]);

        roles.Clear();
        var hr = directory.Resolve(hrAccountId);
        Assert.True(hr.HasRole(SyntheticWorkflowRole.HrIntake));
        Assert.Equal(
            IntakeState.ReadyForItValidation,
            HrIntake.Create(
                "case-snapshot",
                hr,
                directory,
                subjectPersonId,
                siteId,
                [subjectAccountId]).State);

        var disabledHr = directory.Resolve(disabledHrAccountId);
        Assert.False(disabledHr.AccountEnabled);
        var disabled = Assert.Throws<InvalidOperationException>(() =>
            HrIntake.Create(
                "case-disabled-actor",
                disabledHr,
                directory,
                subjectPersonId,
                siteId,
                [subjectAccountId]));
        Assert.Equal("actor_account_disabled", disabled.Message);
    }

    [Fact]
    public void WorkflowRolesAreScopedToTheFormalRequestSite()
    {
        var fixture = LifecycleFixture.Create();

        var intakeError = Assert.Throws<InvalidOperationException>(() =>
            HrIntake.Create(
                "case-offsite-intake",
                fixture.OffsiteHr,
                fixture.Directory,
                fixture.SubjectPersonId,
                fixture.SiteId,
                [fixture.SubjectAccountId]));
        Assert.Equal("actor_site_scope_mismatch", intakeError.Message);

        var lifecycle = fixture.CreateReadyLifecycle("case-offsite-verification");
        var version = lifecycle.Seal(
            fixture.Requester,
            fixture.Subject,
            fixture.Fields);
        var verificationError = Assert.Throws<InvalidOperationException>(() =>
            lifecycle.VerifyIdentity(fixture.OffsiteHr, version.Version));
        Assert.Equal("actor_site_scope_mismatch", verificationError.Message);
        Assert.Empty(lifecycle.Verifications);

        lifecycle.VerifyIdentity(fixture.Hr, version.Version);
        fixture.ConfigureApprovalReadiness();
        var approvalError = Assert.Throws<InvalidOperationException>(() =>
            lifecycle.Approve(
                fixture.OffsiteHr,
                fixture.Subject,
                version.Version,
                fixture.Fields));
        Assert.Equal("actor_site_scope_mismatch", approvalError.Message);
        Assert.Empty(lifecycle.Approvals);
    }

    [Fact]
    public void ConcurrentSupersessionCannotLeaveValidApprovalOnOldVersion()
    {
        var fixture = LifecycleFixture.Create();
        fixture.ConfigureApprovalReadiness();

        for (var iteration = 0; iteration < 64; iteration++)
        {
            var lifecycle = fixture.CreateReadyLifecycle($"case-concurrent-{iteration}");
            var first = lifecycle.Seal(
                fixture.Requester,
                fixture.Subject,
                fixture.Fields);
            lifecycle.VerifyIdentity(fixture.Hr, first.Version);
            Exception? unexpected = null;

            Parallel.Invoke(
                () =>
                {
                    try
                    {
                        lifecycle.Approve(
                            fixture.Hr,
                            fixture.Subject,
                            first.Version,
                            fixture.Fields);
                    }
                    catch (InvalidOperationException error)
                        when (error.Message == "formal_version_superseded")
                    {
                        // Supersession won the race; rejecting the old version is safe.
                    }
                    catch (Exception error)
                    {
                        unexpected = error;
                    }
                },
                () => lifecycle.Seal(
                    fixture.Requester,
                    fixture.Subject,
                    fixture.Fields with { Template = $"template-v2-{iteration}" }));

            Assert.Null(unexpected);
            var supersededVersions = lifecycle.Versions
                .Where(item => item.Superseded)
                .Select(item => item.Version)
                .ToHashSet();
            Assert.DoesNotContain(
                lifecycle.Approvals,
                item => item.Valid && supersededVersions.Contains(item.FormalVersion));
        }
    }

    private sealed class LifecycleFixture
    {
        private LifecycleFixture(
            SyntheticIdentityDirectory directory,
            SyntheticSiteId siteId,
            SyntheticPersonId subjectPersonId,
            SyntheticAccountId subjectAccountId,
            SyntheticActor subject,
            SyntheticActor hr,
            SyntheticActor requester,
            SyntheticActor requesterApproverAlias,
            SyntheticActor issuer,
            SyntheticActor offsiteHr,
            FormalRequestFields fields)
        {
            Directory = directory;
            SiteId = siteId;
            SubjectPersonId = subjectPersonId;
            SubjectAccountId = subjectAccountId;
            Subject = subject;
            Hr = hr;
            Requester = requester;
            RequesterApproverAlias = requesterApproverAlias;
            Issuer = issuer;
            OffsiteHr = offsiteHr;
            Fields = fields;
        }

        public SyntheticIdentityDirectory Directory { get; }
        public SyntheticSiteId SiteId { get; }
        public SyntheticPersonId SubjectPersonId { get; }
        public SyntheticAccountId SubjectAccountId { get; }
        public SyntheticActor Subject { get; }
        public SyntheticActor Hr { get; }
        public SyntheticActor Requester { get; }
        public SyntheticActor RequesterApproverAlias { get; }
        public SyntheticActor Issuer { get; }
        public SyntheticActor OffsiteHr { get; }
        public FormalRequestFields Fields { get; }

        public static LifecycleFixture Create()
        {
            var siteId = new SyntheticSiteId("test-site");
            var subjectPersonId = new SyntheticPersonId("subject-person");
            var subjectAccountId = new SyntheticAccountId("subject-account");
            var hrAccountId = new SyntheticAccountId("hr-account");
            var requesterPersonId = new SyntheticPersonId("requester-person");
            var requesterAccountId = new SyntheticAccountId("requester-account");
            var requesterAliasAccountId = new SyntheticAccountId("requester-alias-account");
            var issuerAccountId = new SyntheticAccountId("issuer-account");
            var offsiteHrAccountId = new SyntheticAccountId("offsite-hr-account");
            var otherSiteId = new SyntheticSiteId("other-site");
            var directory = SyntheticIdentityDirectory.Create(
            [
                new(
                    subjectPersonId,
                    subjectAccountId,
                    "Synthetic Subject",
                    siteId,
                    new HashSet<SyntheticWorkflowRole>()),
                new(
                    new("hr-person"),
                    hrAccountId,
                    "Synthetic HR",
                    siteId,
                    new HashSet<SyntheticWorkflowRole>
                    {
                        SyntheticWorkflowRole.HrIntake,
                        SyntheticWorkflowRole.HrVerifier,
                        SyntheticWorkflowRole.HrApprover
                    }),
                new(
                    requesterPersonId,
                    requesterAccountId,
                    "Synthetic IT Requester",
                    siteId,
                    new HashSet<SyntheticWorkflowRole>
                    {
                        SyntheticWorkflowRole.ItFormalRequester
                    }),
                new(
                    requesterPersonId,
                    requesterAliasAccountId,
                    "Synthetic Requester Alias",
                    siteId,
                    new HashSet<SyntheticWorkflowRole>
                    {
                        SyntheticWorkflowRole.HrApprover
                    }),
                new(
                    new("issuer-person"),
                    issuerAccountId,
                    "Synthetic Issuer",
                    siteId,
                    new HashSet<SyntheticWorkflowRole>
                    {
                        SyntheticWorkflowRole.ItIssuer
                    }),
                new(
                    new("offsite-hr-person"),
                    offsiteHrAccountId,
                    "Offsite HR",
                    otherSiteId,
                    new HashSet<SyntheticWorkflowRole>
                    {
                        SyntheticWorkflowRole.HrIntake,
                        SyntheticWorkflowRole.HrVerifier,
                        SyntheticWorkflowRole.HrApprover
                    })
            ]);
            var fields = new FormalRequestFields(
                siteId,
                subjectPersonId,
                subjectAccountId,
                FormalRequestFields.RequiredIntendedUse,
                "fake-device-1",
                FormalRequestFields.RequiredSlot,
                PublicRequestDigest,
                "fake-authority-1",
                "template-v1",
                "policy-v1");

            return new LifecycleFixture(
                directory,
                siteId,
                subjectPersonId,
                subjectAccountId,
                directory.Resolve(subjectAccountId),
                directory.Resolve(hrAccountId),
                directory.Resolve(requesterAccountId),
                directory.Resolve(requesterAliasAccountId),
                directory.Resolve(issuerAccountId),
                directory.Resolve(offsiteHrAccountId),
                fields);
        }

        public FormalRequestLifecycle CreateReadyLifecycle(string caseId = "case-ready")
        {
            var intake = HrIntake.Create(
                caseId,
                Hr,
                Directory,
                SubjectPersonId,
                SiteId,
                [SubjectAccountId]);
            return CreateLifecycle(intake);
        }

        public FormalRequestLifecycle CreateLifecycle(HrIntake intake) => new(
            intake,
            new SyntheticSimulationPolicySource(
                SimulationPolicySnapshot.Create(
                    "quarterdeck-test-policy",
                    Fields.PolicyOptionsVersion,
                    ["physical-piv-only"])),
            new SyntheticSimulationApprovalClock(
                new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero)));

        public void ConfigureApprovalReadiness()
        {
            SetContainmentState(SyntheticContainmentState.Clear);
            SetFixtureState(
                "SetDeviceQualificationState",
                Fields.FakeDeviceId,
                SyntheticDeviceQualificationState.Qualified);
            SetFixtureState(
                "SetAuthorityAvailabilityState",
                Fields.FakeAuthority,
                SyntheticAuthorityAvailabilityState.Available);
        }

        public void SetContainmentState(SyntheticContainmentState state) =>
            SetFixtureState("SetContainmentState", SubjectAccountId, state);

        private void SetFixtureState(string methodName, params object[] arguments)
        {
            var method = typeof(SyntheticIdentityDirectory).GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"Missing fixture method: {methodName}");
            _ = Assert.IsType<long>(method.Invoke(Directory, arguments));
        }
    }
}
