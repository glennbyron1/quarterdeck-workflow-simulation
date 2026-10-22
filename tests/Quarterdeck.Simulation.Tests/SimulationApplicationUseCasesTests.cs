using Quarterdeck.Application;
using Quarterdeck.App.Pages.Cases;
using Quarterdeck.Core.Simulation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Quarterdeck.Presentation;

namespace Quarterdeck.Persistence.Tests;

public sealed class SimulationApplicationUseCasesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "quarterdeck-application-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Concurrent_generated_intakes_allocate_unique_ids_and_continue_after_restart()
    {
        var app = Create();
        using var start = new Barrier(8);
        var workers = Enumerable.Range(0, 8).Select(_ => Task.Factory.StartNew(() =>
        {
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(30)));
            return Enumerable.Range(0, 8).Select(_ => app.CreateIntake(new(
                string.Empty, "person-1", "site-1", "hr-account", []))).ToArray();
        }, CancellationToken.None, TaskCreationOptions.LongRunning,
            TaskScheduler.Default)).ToArray();
        var results = (await Task.WhenAll(workers)).SelectMany(value => value).ToArray();
        Assert.All(results, result => Assert.True(result.Succeeded, result.SafeResultCode));
        var expected = Enumerable.Range(1, 64).Select(value => $"SIM-QD-{value:000000}").ToArray();
        Assert.Equal(expected, results.Select(value => value.CaseId).Order(StringComparer.Ordinal));
        Assert.Equal(64, Scalar("SELECT COUNT(*) FROM cases"));
        Assert.Equal(64, Scalar("SELECT COUNT(*) FROM audit_events WHERE action_code='intake_created'"));
        var restarted = BuildUseCases(Paths());
        Assert.All(expected, id => Assert.NotNull(restarted.ReadCase(id)));
        var next = restarted.CreateIntake(new(string.Empty, "person-1", "site-1", "hr-account", []));
        Assert.True(next.Succeeded, next.SafeResultCode);
        Assert.Equal("SIM-QD-000065", next.CaseId);
    }

    [Fact]
    public void Failed_intake_after_allocation_does_not_reuse_number()
    {
        var app = Create();
        Execute("CREATE TRIGGER test_fail_intake BEFORE INSERT ON cases BEGIN SELECT RAISE(ABORT,'test_write_failure'); END;");
        var failed = app.CreateIntake(new(string.Empty, "person-1", "site-1", "hr-account", []));
        Assert.False(failed.Succeeded);
        Assert.Equal(1, Scalar("SELECT last_value FROM case_number_sequence"));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM cases"));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM audit_events WHERE action_code='intake_created'"));
        Execute("DROP TRIGGER test_fail_intake;");
        var restarted = BuildUseCases(Paths());
        var next = restarted.CreateIntake(new(string.Empty, "person-1", "site-1", "hr-account", []));
        Assert.True(next.Succeeded, next.SafeResultCode);
        Assert.Equal("SIM-QD-000002", next.CaseId);
        Assert.Null(restarted.ReadCase("SIM-QD-000001"));
    }

    [Fact]
    public void Abandoned_allocation_is_not_reused_after_restart()
    {
        Create();
        Assert.Equal("SIM-QD-000001", Repository().AllocateCaseId());
        var restarted = BuildUseCases(Paths());
        var next = restarted.CreateIntake(new(string.Empty, "person-1", "site-1", "hr-account", []));
        Assert.True(next.Succeeded, next.SafeResultCode);
        Assert.Equal("SIM-QD-000002", next.CaseId);
        Assert.Null(restarted.ReadCase("SIM-QD-000001"));
    }

    [Fact]
    public void Seeded_and_other_departments_validate_and_preserve_labels()
    {
        var app = Create();
        CreateSyntheticIntakeRequest Request(string department, string? other = null) =>
            new(string.Empty, "person-1", "site-1", "hr-account", [],
                "TEST Example", department, other);
        Assert.False(app.CreateIntake(Request("UNKNOWN")).Succeeded);
        Assert.False(app.CreateIntake(Request("OTHER")).Succeeded);
        Assert.False(app.CreateIntake(Request("OTHER", new string('x', 81))).Succeeded);
        Assert.False(app.CreateIntake(Request("IT", "Unexpected override")).Succeeded);
        var seeded = app.CreateIntake(Request("IT"));
        var other = app.CreateIntake(Request("OTHER", "Emergency Planning"));
        Assert.True(seeded.Succeeded, seeded.SafeResultCode);
        Assert.True(other.Succeeded, other.SafeResultCode);
        var restarted = BuildUseCases(Paths());
        Assert.Equal("Information Technology", restarted.ReadCase(seeded.CaseId)!.DepartmentLabel);
        Assert.Equal("Emergency Planning", restarted.ReadCase(other.CaseId)!.DepartmentLabel);
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM case_intake_profiles WHERE department_code='OTHER' AND other_department_review_required=1"));
        Assert.Equal(2, Scalar("SELECT COUNT(*) FROM cases"));
    }

    [Fact]
    public void Return_to_hr_requires_role_reason_revision_and_fresh_resolution_after_resubmit()
    {
        var app=Create();
        Assert.True(app.CreateIntake(new("CASE-RETURN","person-1","site-1","hr-account",[])).Succeeded);
        Assert.True(app.ResolveWaitingForIt(new("CASE-RETURN",0,"subject-account","it-account","confirmed")).Succeeded);
        var request=new HrCorrectionRequest("CASE-RETURN",1,"it-account","Username needs clarification");
        Assert.False(app.ReturnToHr(request with {ActorAccountId="hr-account"}).Succeeded);
        Assert.False(app.ReturnToHr(request with {Comment=""}).Succeeded);
        Assert.False(app.ReturnToHr(request with {Comment=new string('x',501)}).Succeeded);
        Assert.False(app.ReturnToHr(request with {ExpectedRevision=0}).Succeeded);
        Assert.True(app.ReturnToHr(request).Succeeded);
        Assert.False(app.ReturnToHr(request).Succeeded);
        var returned=app.ReadCase("CASE-RETURN")!;
        Assert.Equal(WorkflowPresentationCondition.NeedsHrCorrection,returned.Condition);
        Assert.Equal(WorkflowNextRole.HrRequester,returned.NextAction.Role);
        Assert.False(app.ResolveWaitingForIt(new("CASE-RETURN",returned.CaseRevision,"subject-account","it-account","confirmed")).Succeeded);
        Assert.False(app.SealFormalRequest(new("CASE-RETURN",returned.CaseRevision,"it-account",Fields())).Succeeded);
        var restarted=BuildUseCases(Paths());
        Assert.Equal("Username needs clarification",Assert.Single(restarted.ReadHrCorrections("CASE-RETURN")).Comment);
        var reply=new HrCorrectionRequest("CASE-RETURN",returned.CaseRevision,"hr-account","Please use this account","subject-account");
        Assert.False(restarted.ResubmitToIt(reply with {ActorAccountId="it-account"}).Succeeded);
        Assert.True(restarted.ResubmitToIt(reply).Succeeded);
        Assert.False(restarted.ResubmitToIt(reply).Succeeded);
        var resubmitted=restarted.ReadCase("CASE-RETURN")!;
        Assert.Equal(WorkflowPresentationCondition.WaitingForIt,resubmitted.Condition);
        Assert.False(restarted.SealFormalRequest(new("CASE-RETURN",resubmitted.CaseRevision,"it-account",Fields())).Succeeded);
        Assert.True(restarted.ResolveWaitingForIt(new("CASE-RETURN",resubmitted.CaseRevision,"subject-account","it-account","reconfirmed")).Succeeded);
        Assert.True(restarted.SealFormalRequest(new("CASE-RETURN",restarted.ReadCase("CASE-RETURN")!.CaseRevision,"it-account",Fields())).Succeeded);
        Assert.Equal(2,restarted.ReadHrCorrections("CASE-RETURN").Count);
        Assert.Equal(0,restarted.ReadSimulationCounters().FakeDispatchCalls);
    }

    [Fact]
    public void Registered_fixture_survives_hr_return_reconfirmation_and_restart()
    {
        var app = Create();
        const string caseId = "CASE-FIXTURE-RETURN";
        Assert.True(app.CreateIntake(new(caseId, "person-3", "site-1",
            "hr-account", [])).Succeeded);
        Assert.True(app.RegisterSimulationAccount(new(caseId, 0,
            "person-3-account", "it-account")).Succeeded);
        Assert.True(app.ReturnToHr(new(caseId, app.ReadCase(caseId)!.CaseRevision,
            "it-account", "Confirm requested username")).Succeeded);
        Assert.True(app.ResubmitToIt(new(caseId, app.ReadCase(caseId)!.CaseRevision,
            "hr-account", "Username confirmed", "person-3-account")).Succeeded);
        Assert.True(app.ResolveWaitingForIt(new(caseId, app.ReadCase(caseId)!.CaseRevision,
            "person-3-account", "it-account", "reconfirmed")).Succeeded);

        var restarted = BuildUseCases(Paths());
        var result = restarted.SealFormalRequest(new(caseId,
            restarted.ReadCase(caseId)!.CaseRevision, "it-account",
            new("site-1", "person-3", "person-3-account",
                FormalRequestFields.RequiredIntendedUse, "FAKE-DEVICE-001",
                FormalRequestFields.RequiredSlot, new string('a', 64),
                "quarterdeck-fake-authority", "fake-piv-authentication", "v1")));
        Assert.True(result.Succeeded, result.SafeResultCode);
        Assert.Equal(0, restarted.ReadSimulationCounters().FakeDispatchCalls);
    }

    [Fact]
    public void Return_cannot_rewind_a_sealed_case()
    {
        var app=CreateWithFormalVersion();
        var before=app.ReadCase("CASE-FORMAL")!;
        Assert.False(app.ReturnToHr(new("CASE-FORMAL",before.CaseRevision,"it-account","Return after sealing")).Succeeded);
        Assert.Equal(before.CaseRevision,app.ReadCase("CASE-FORMAL")!.CaseRevision);
        Assert.Empty(app.ReadHrCorrections("CASE-FORMAL"));
    }

    [Fact]
    public void Return_comment_write_failure_rolls_back_state_and_history()
    {
        var app=Create();
        Assert.True(app.CreateIntake(new("CASE-FAULT","person-1","site-1","hr-account",[])).Succeeded);
        Execute("CREATE TRIGGER test_block_correction BEFORE INSERT ON audit_event_fields BEGIN SELECT RAISE(ABORT,'test_write_failure'); END;");
        Assert.False(app.ReturnToHr(new("CASE-FAULT",0,"it-account","Required correction")).Succeeded);
        Assert.Equal(0,app.ReadCase("CASE-FAULT")!.CaseRevision);
        Assert.Equal(WorkflowPresentationCondition.WaitingForIt,app.ReadCase("CASE-FAULT")!.Condition);
        Assert.Empty(app.ReadHrCorrections("CASE-FAULT"));
        Assert.Equal(0,Scalar("SELECT COUNT(*) FROM audit_events WHERE action_code='returned_to_hr'"));
    }

    [Fact]
    public void Hr_name_change_routes_to_it_persists_and_preserves_original_case()
    {
        var app = Create();
        Assert.True(app.CreateIntake(new("CASE-NAME", "person-1", "site-1","hr-account",[])).Succeeded);
        Assert.True(app.ResolveWaitingForIt(new("CASE-NAME",0,"subject-account","it-account","confirmed")).Succeeded);
        var original = app.ReadCase("CASE-NAME")!;
        var request = new NameChangeRequest("CASE-NAME",original.CaseRevision,"Test New Name",null,"hr-account");
        Assert.False(app.RequestNameChange(request with { ActorAccountId="it-account" }).Succeeded);
        Assert.False(app.RequestNameChange(request with { ExpectedRevision=999 }).Succeeded);
        Assert.False(app.RequestNameChange(request with { Comment=new string('x',501) }).Succeeded);
        Assert.True(app.RequestNameChange(request).Succeeded);
        Assert.False(app.RequestNameChange(request).Succeeded);
        var restarted = BuildUseCases(Paths());
        var row = Assert.Single(restarted.ReadNameChanges());
        Assert.Equal("person-1",row.PersonId);
        Assert.Equal("subject-account",row.Username);
        Assert.Null(row.Comment);
        Assert.False(restarted.CompleteNameChange(row.RequestId,"hr-account","Checked").Succeeded);
        Assert.False(restarted.CompleteNameChange(row.RequestId,"it-account","").Succeeded);
        Assert.True(restarted.CompleteNameChange(row.RequestId,"it-account","Simulated updates checked").Succeeded);
        Assert.False(restarted.CompleteNameChange(row.RequestId,"it-account","Again").Succeeded);
        var finished = Assert.Single(BuildUseCases(Paths()).ReadNameChanges());
        Assert.Equal("it-account",finished.CompletedBy);
        Assert.Equal(original.PersonReference,restarted.ReadCase("CASE-NAME")!.PersonReference);
        Assert.Equal(original.CaseRevision,restarted.ReadCase("CASE-NAME")!.CaseRevision);
        Assert.Equal(original.DisplayLabel,restarted.ReadCase("CASE-NAME")!.DisplayLabel);
    }

    [Fact]
    public void Intake_and_resolution_round_trip_through_sqlite_projection()
    {
        var useCases = Create();
        Assert.True(useCases.CreateIntake(new("CASE-001", "person-1", "site-1",
            "hr-account", ["wrong-account"])).Succeeded);
        var waiting = Assert.Single(useCases.ReadWorkQueue(new()));
        Assert.Equal(WorkflowPresentationCondition.WaitingForIt, waiting.Condition);

        var result = useCases.ResolveWaitingForIt(new("CASE-001", 0,
            "subject-account", "it-account", "corrected submitted account"));

        Assert.True(result.Succeeded);
        Assert.Equal("account_resolution_recorded", result.SafeResultCode);
        var projected = useCases.ReadCase("CASE-001")!;
        Assert.Equal(WorkflowPresentationCondition.ReadyForItValidation, projected.Condition);
        Assert.Equal(2, projected.Timeline.Count);
    }

    [Fact]
    public void Explicit_simulation_fixture_allows_arbitrary_intake_and_survives_restart()
    {
        var useCases = Create();
        Assert.True(useCases.CreateIntake(new("CASE-FIXTURE", "person-3", "site-1",
            "hr-account", [], "Jordan Lee", "IT")).Succeeded);

        var result = useCases.RegisterSimulationAccount(new("CASE-FIXTURE", 0,
            "person-3-account", "it-account"));

        Assert.True(result.Succeeded, result.SafeResultCode);
        Assert.Equal("account_resolution_recorded", result.SafeResultCode);
        Assert.Equal(WorkflowPresentationCondition.ReadyForItValidation,
            useCases.ReadCase("CASE-FIXTURE")!.Condition);

        var restarted = BuildUseCases(Paths());
        var current = restarted.ReadCase("CASE-FIXTURE")!;
        var sealedResult = restarted.SealFormalRequest(new("CASE-FIXTURE",
            current.CaseRevision, "it-account", new("site-1", "person-3",
                "person-3-account", FormalRequestFields.RequiredIntendedUse,
                "FAKE-DEVICE-001", FormalRequestFields.RequiredSlot,
                new string('a', 64), "quarterdeck-fake-authority",
                "fake-piv-authentication", "v1")));
        Assert.True(sealedResult.Succeeded, sealedResult.SafeResultCode);
    }

    [Fact]
    public void Unauthorized_actor_cannot_create_simulation_fixture()
    {
        var useCases = Create();
        Assert.True(useCases.CreateIntake(new("CASE-FIXTURE-DENIED", "person-3",
            "site-1", "hr-account", [])).Succeeded);

        var denied = useCases.RegisterSimulationAccount(new("CASE-FIXTURE-DENIED", 0,
            "person-3-account", "hr-account"));

        Assert.False(denied.Succeeded);
        Assert.Equal("actor_not_authorized", denied.SafeResultCode);
        var unresolved = useCases.ResolveWaitingForIt(new("CASE-FIXTURE-DENIED", 0,
            "person-3-account", "it-account", "account_created"));
        Assert.False(unresolved.Succeeded);
        Assert.Equal("exact_account_unresolved", unresolved.SafeResultCode);
    }

    [Fact]
    public void Fixture_cannot_replace_existing_account_or_accept_stale_revision()
    {
        var useCases = Create();
        Assert.True(useCases.CreateIntake(new("CASE-FIXTURE-BOUNDARY", "person-3",
            "site-1", "hr-account", [])).Succeeded);
        Assert.False(useCases.RegisterSimulationAccount(new("CASE-FIXTURE-BOUNDARY", 0,
            "subject-account", "it-account")).Succeeded);
        Assert.False(useCases.RegisterSimulationAccount(new("CASE-FIXTURE-BOUNDARY", 99,
            "person-3-account", "it-account")).Succeeded);
        Assert.False(useCases.ResolveWaitingForIt(new("CASE-FIXTURE-BOUNDARY", 0,
            "person-3-account", "it-account", "account_created")).Succeeded);
        Assert.True(useCases.RegisterSimulationAccount(new("CASE-FIXTURE-BOUNDARY", 0,
            "person-3-account", "it-account")).Succeeded);
    }

    [Fact]
    public void Ordinary_resolution_cannot_forge_fixture_restore_marker()
    {
        var useCases = Create();
        Assert.True(useCases.CreateIntake(new("CASE-MARKER", "person-1", "site-1",
            "hr-account", [])).Succeeded);
        Assert.False(useCases.ResolveWaitingForIt(new("CASE-MARKER", 0,
            "subject-account", "it-account", "simulation_fixture_created_and_confirmed")).Succeeded);
        Assert.False(useCases.ResolveWaitingForIt(new("CASE-MARKER", 0,
            "subject-account", "it-account", " simulation_fixture_created_and_confirmed ")).Succeeded);
        Assert.Equal(WorkflowPresentationCondition.WaitingForIt,
            useCases.ReadCase("CASE-MARKER")!.Condition);
    }

    [Fact]
    public void Public_application_path_seals_version_and_records_required_gate()
    {
        var useCases = Create();
        Assert.True(useCases.CreateIntake(new("CASE-PUBLIC-PATH", "person-1", "site-1",
            "hr-account", [])).Succeeded);
        Assert.True(useCases.ResolveWaitingForIt(new("CASE-PUBLIC-PATH", 0,
            "subject-account", "it-account", "exact synthetic account")).Succeeded);

        var resolved = useCases.ReadCase("CASE-PUBLIC-PATH")!;
        var sealedResult = useCases.SealFormalRequest(new("CASE-PUBLIC-PATH",
            resolved.CaseRevision, "it-account", Fields()));

        Assert.True(sealedResult.Succeeded, sealedResult.SafeResultCode);
        Assert.Equal("formal_version_sealed", sealedResult.SafeResultCode);
        var sealedCase = useCases.ReadCase("CASE-PUBLIC-PATH")!;
        Assert.Equal(1, sealedCase.FormalVersion.Version);
        Assert.Equal(WorkflowPresentationCondition.AwaitingHrVerification,
            sealedCase.Condition);
        var beforeGate = useCases.ReadPolicyApproval("CASE-PUBLIC-PATH")!;
        Assert.Contains(beforeGate.MandatoryGates, gate =>
            gate.GateCode == "directory_ready" && !gate.Satisfied);

        var gateResult = useCases.ObserveMandatoryGate(new("CASE-PUBLIC-PATH",
            sealedCase.CaseRevision, 1, "it-account", "directory_ready"));

        Assert.True(gateResult.Succeeded, gateResult.SafeResultCode);
        Assert.Contains(useCases.ReadPolicyApproval("CASE-PUBLIC-PATH")!.MandatoryGates,
            gate => gate.GateCode == "directory_ready" && gate.Satisfied);
    }

    [Fact]
    public void Wrong_role_cannot_resolve_and_does_not_mutate_sqlite()
    {
        var useCases = Create();
        Assert.True(useCases.CreateIntake(new("CASE-002", "person-1", "site-1",
            "hr-account", [])).Succeeded);

        var result = useCases.ResolveWaitingForIt(new("CASE-002", 0,
            "subject-account", "hr-account", "corrected"));

        Assert.False(result.Succeeded);
        Assert.Equal("actor_not_authorized", result.SafeResultCode);
        Assert.Single(useCases.ReadCase("CASE-002")!.Timeline);
        Assert.Single(useCases.ReadAuditTrail("CASE-002"));
    }

    [Fact]
    public void One_thousand_and_one_denials_preserve_capacity_and_make_no_fake_calls()
    {
        var useCases = Create();
        Assert.True(useCases.CreateIntake(new("CASE-DENIAL-LOAD", "person-1", "site-1",
            "hr-account", [])).Succeeded);
        var beforeRevision = useCases.ReadCase("CASE-DENIAL-LOAD")!.CaseRevision;
        var beforeAudit = useCases.ReadAuditTrail("CASE-DENIAL-LOAD").Count;
        var beforeCalls = useCases.ReadSimulationCounters().FakeDispatchCalls;

        for (var attempt = 0; attempt < 1001; attempt++)
        {
            var denied = useCases.ResolveWaitingForIt(new("CASE-DENIAL-LOAD",
                beforeRevision, "subject-account", "hr-account", "not-authorized"));
            Assert.False(denied.Succeeded);
            Assert.Equal("actor_not_authorized", denied.SafeResultCode);
        }

        Assert.Equal(beforeRevision,
            useCases.ReadCase("CASE-DENIAL-LOAD")!.CaseRevision);
        Assert.Equal(beforeAudit, useCases.ReadAuditTrail("CASE-DENIAL-LOAD").Count);
        Assert.Equal(1001L, Scalar("SELECT count(*) FROM command_denial_events WHERE case_id='CASE-DENIAL-LOAD'"));
        var denials = useCases.ReadDeniedCommands("CASE-DENIAL-LOAD");
        Assert.Equal(1001, denials.Count);
        Assert.All(denials, denial =>
        {
            Assert.Equal("hr-account", denial.ActorReference);
            Assert.Equal("resolve_waiting_for_it", denial.CommandCode);
            Assert.Equal("actor_not_authorized", denial.SafeResultCode);
        });
        Assert.Equal(beforeCalls, useCases.ReadSimulationCounters().FakeDispatchCalls);

        var valid = useCases.ResolveWaitingForIt(new("CASE-DENIAL-LOAD",
            beforeRevision, "subject-account", "it-account", "corrected"));
        Assert.True(valid.Succeeded, valid.SafeResultCode);
    }

    [Fact]
    public void Invalid_attacker_values_are_not_copied_into_the_denial_ledger()
    {
        var useCases = Create();
        const string attackerValue = "attacker-controlled-secret value";

        var denied = useCases.CreateIntake(new(attackerValue, "person-1", "site-1",
            attackerValue, []));

        Assert.False(denied.Succeeded);
        Assert.Equal("command_input_invalid", denied.SafeResultCode);
        Assert.Equal(1L, Scalar("SELECT count(*) FROM command_denial_events"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM command_denial_events WHERE case_id IS NOT NULL"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM command_denial_events WHERE command_code LIKE '%attacker%' OR safe_result_code LIKE '%attacker%'"));

        Assert.True(useCases.CreateIntake(new("CASE-SPOOF", "person-1", "site-1",
            "hr-account", [])).Succeeded);
        var stale = useCases.ResolveWaitingForIt(new("CASE-SPOOF", 99,
            "subject-account", "it-account", null));
        Assert.False(stale.Succeeded);
        Assert.Equal("case_revision_stale", stale.SafeResultCode);
        var attributed = Assert.Single(useCases.ReadDeniedCommands("CASE-SPOOF"));
        Assert.Equal("it-account", attributed.ActorReference);
        var spoofed = useCases.ResolveWaitingForIt(new("CASE-SPOOF", 0,
            "subject-account", "spoof-account", null));
        Assert.False(spoofed.Succeeded);
        Assert.Equal("command_rejected", spoofed.SafeResultCode);
        var spoofDenial = Assert.Single(useCases.ReadDeniedCommands("CASE-SPOOF"),
            denial => denial.ActorReference == "unresolved");
        Assert.Equal("unresolved", spoofDenial.ActorReference);
        Assert.Equal(0L, Scalar("SELECT count(*) FROM command_denial_events WHERE actor_account_id='spoof-account'"));
    }

    [Fact]
    public void Denial_append_failure_returns_store_unavailable_instead_of_claiming_audited_denial()
    {
        var useCases = Create();
        Execute("DROP TABLE command_denial_events");

        var denied = useCases.ResolveWaitingForIt(new("CASE-MISSING", 0,
            "subject-account", "hr-account", null));

        Assert.False(denied.Succeeded);
        Assert.Equal("command_store_unavailable", denied.SafeResultCode);
    }

    [Fact]
    public void Stale_post_cannot_repeat_resolution_or_append_history()
    {
        var useCases = Create();
        Assert.True(useCases.CreateIntake(new("CASE-003", "person-1", "site-1",
            "hr-account", [])).Succeeded);
        var request = new ResolveWaitingForItRequest("CASE-003", 0,
            "subject-account", "it-account", "corrected");
        Assert.True(useCases.ResolveWaitingForIt(request).Succeeded);

        var replay = useCases.ResolveWaitingForIt(request);

        Assert.False(replay.Succeeded);
        Assert.Equal("case_revision_stale", replay.SafeResultCode);
        Assert.Equal(2, useCases.ReadCase("CASE-003")!.Timeline.Count);
    }

    [Fact]
    public void Browser_supplied_account_from_another_site_fails_closed()
    {
        var useCases = Create();
        Assert.True(useCases.CreateIntake(new("CASE-004", "person-1", "site-1",
            "hr-account", [])).Succeeded);

        var result = useCases.ResolveWaitingForIt(new("CASE-004", 0,
            "other-account", "it-account", "corrected"));

        Assert.False(result.Succeeded);
        Assert.Equal("exact_account_unresolved", result.SafeResultCode);
        Assert.Equal(WorkflowPresentationCondition.WaitingForIt,
            useCases.ReadCase("CASE-004")!.Condition);
    }

    [Fact]
    public void Resolve_handler_uses_post_redirect_get_and_returns_only_safe_code()
    {
        var fake = new RecordingUseCases();
        var page = new DetailsModel(fake)
        {
            ExpectedCaseRevision = 8,
            ExactAccountId = "subject-account",
            CorrectionReason = "corrected"
        };
        page.ModelState.AddModelError("RecoveryFindingId", "Unrelated form is empty");

        var response = Assert.IsType<RedirectToPageResult>(page.OnPostResolve("CASE-PRG"));

        Assert.Equal("/Cases/Details", response.PageName);
        Assert.Equal("case_revision_stale", page.CommandResultCode);
        Assert.Equal(new ResolveWaitingForItRequest("CASE-PRG", 8,
            "subject-account", "TEST-IT-ACCOUNT", "corrected"), fake.Request);
    }

    [Fact]
    public void Verification_and_approval_handlers_use_prg_and_server_owned_hr_actor()
    {
        var fake = new RecordingUseCases();
        var page = new ReviewModel(fake) { ExpectedCaseRevision = 9,
            SiteId = "site-1", PersonId = "person-1", AccountId = "subject-account",
            IntendedUse = FormalRequestFields.RequiredIntendedUse,
            FakeDeviceId = "FAKE-DEVICE-001", Slot = FormalRequestFields.RequiredSlot,
            SyntheticPublicRequestDigest = new string('a', 64),
            FakeAuthority = "quarterdeck-fake-authority", Template = "template",
            PolicyOptionsVersion = "v1" };

        Assert.IsType<RedirectToPageResult>(page.OnPostVerify("CASE-PRG", 1));
        Assert.Equal("TEST-HR-ACCOUNT", fake.Verification!.ActorAccountId);
        Assert.IsType<RedirectToPageResult>(page.OnPostApprove("CASE-PRG", 1));
        Assert.Equal("TEST-HR-ACCOUNT", fake.Approval!.ActorAccountId);
        Assert.Equal("subject-account", fake.Approval.ObservedFields.AccountId);
    }

    [Fact]
    public void Fake_issuance_handlers_use_prg_and_server_owned_it_issuer()
    {
        var fake = new RecordingUseCases();
        var page = new DetailsModel(fake)
        {
            ExpectedCaseRevision = 12,
            SyntheticPublicRequestReference = "synthetic-request-1"
        };

        Assert.IsType<RedirectToPageResult>(
            page.OnPostStartFakeIssuance("CASE-PRG", 3));
        Assert.Equal("TEST-IT-ACCOUNT", fake.StartIssuance!.ActorAccountId);
        Assert.Equal("synthetic-request-1",
            fake.StartIssuance.SyntheticPublicRequestReference);

        Assert.IsType<RedirectToPageResult>(page.OnPostObserveFakeIssuance(
            "CASE-PRG", "00000000-0000-0000-0000-000000000401"));
        Assert.Equal("TEST-IT-ACCOUNT", fake.ObserveIssuance!.ActorAccountId);
        page.RecoveryFindingId = "00000000-0000-0000-0000-000000000402";
        page.RecoveryReasonCode = "exact_terminal_review";
        Assert.IsType<RedirectToPageResult>(page.OnPostReconcileFakeIssuance(
            "CASE-PRG", "00000000-0000-0000-0000-000000000401"));
        Assert.Equal("TEST-IT-ACCOUNT", fake.ReconcileIssuance!.ActorAccountId);
    }

    [Fact]
    public void Exact_version_review_has_ten_fields_and_verification_then_approval_persist()
    {
        var useCases = CreateWithFormalVersion();
        var review = useCases.ReadVersionReview("CASE-FORMAL", 1)!;
        Assert.Equal(10, review.BoundFields.Count);
        Assert.True(review.IsCurrent);

        var verified = useCases.VerifyIdentity(new("CASE-FORMAL",
            useCases.ReadCase("CASE-FORMAL")!.CaseRevision, 1, "hr-account"));
        Assert.True(verified.Succeeded, verified.SafeResultCode);
        Assert.Equal(WorkflowPresentationCondition.AwaitingExactApproval,
            useCases.ReadCase("CASE-FORMAL")!.Condition);

        var approved = useCases.ApproveExactVersion(new("CASE-FORMAL",
            useCases.ReadCase("CASE-FORMAL")!.CaseRevision, 1,
            "hr-account", Fields()));
        Assert.True(approved.Succeeded);
        Assert.Equal("exact_approval_recorded", approved.SafeResultCode);
        Assert.Equal(WorkflowPresentationCondition.ReadyForSimulatedIssuance,
            useCases.ReadCase("CASE-FORMAL")!.Condition);
    }

    [Fact]
    public void Changed_bound_field_and_stale_version_fail_without_approval()
    {
        var useCases = CreateWithFormalVersion();
        var verified = useCases.VerifyIdentity(new("CASE-FORMAL",
            useCases.ReadCase("CASE-FORMAL")!.CaseRevision, 1, "hr-account"));
        Assert.True(verified.Succeeded, verified.SafeResultCode);
        var changed = Fields() with { AccountId = "other-account" };

        var revision = useCases.ReadCase("CASE-FORMAL")!.CaseRevision;
        var rejected = useCases.ApproveExactVersion(new("CASE-FORMAL", revision, 1,
            "hr-account", changed));
        var stale = useCases.VerifyIdentity(new("CASE-FORMAL", 2, 1, "hr-account"));

        Assert.Equal("bound_fields_changed", rejected.SafeResultCode);
        Assert.Equal("case_revision_stale", stale.SafeResultCode);
        Assert.Equal("Not approved", useCases.ReadCase("CASE-FORMAL")!.Approval.Status);
    }

    [Fact]
    public void Wrong_role_with_same_display_label_cannot_verify()
    {
        var useCases = CreateWithFormalVersion();
        var result = useCases.VerifyIdentity(new("CASE-FORMAL",
            useCases.ReadCase("CASE-FORMAL")!.CaseRevision, 1, "lookalike-account"));
        Assert.False(result.Succeeded);
        Assert.Equal("actor_not_authorized", result.SafeResultCode);
        Assert.Equal(WorkflowPresentationCondition.AwaitingHrVerification,
            useCases.ReadCase("CASE-FORMAL")!.Condition);
    }

    [Fact]
    public void Review_of_missing_version_returns_no_action_projection()
    {
        var useCases = CreateWithFormalVersion();
        Assert.Null(useCases.ReadVersionReview("CASE-FORMAL", 99));
        Assert.Null(useCases.ReadVersionReview("missing", 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Missing_or_ambiguous_submitted_account_stays_waiting_for_it(int count)
    {
        var useCases = Create();
        var candidates = Enumerable.Range(0, count)
            .Select(index => index == 0 ? "subject-account" : "other-account")
            .ToArray();
        Assert.True(useCases.CreateIntake(new($"CASE-CANDIDATE-{count}", "person-1",
            "site-1", "hr-account", candidates)).Succeeded);
        Assert.Equal(WorkflowPresentationCondition.WaitingForIt,
            useCases.ReadCase($"CASE-CANDIDATE-{count}")!.Condition);
    }

    [Fact]
    public void Wrong_formal_version_is_rejected_before_identity_mutation()
    {
        var useCases = CreateWithFormalVersion();
        var result = useCases.VerifyIdentity(new("CASE-FORMAL",
            useCases.ReadCase("CASE-FORMAL")!.CaseRevision, 99, "hr-account"));
        Assert.Equal("formal_version_superseded", result.SafeResultCode);
        Assert.Equal(WorkflowPresentationCondition.AwaitingHrVerification,
            useCases.ReadCase("CASE-FORMAL")!.Condition);
    }

    [Fact]
    public void Superseded_review_is_read_only_and_current_diff_names_changed_field()
    {
        var useCases = CreateWithFormalVersion();
        var repository = Repository();
        var first = repository.Load("CASE-FORMAL")!.FormalVersions.Single();
        var policy = SimulationPolicySnapshot.CreateClassified("policy-authority", "v1",
            [new("photo-required", SimulationPolicyOptionClassification.Required)]);
        var replacement = first.Request with { Template = "replacement-template" };
        var digest = SealedRequestBinding.Compute(ToFields(replacement), policy, 4);
        var currentRevision = useCases.ReadCase("CASE-FORMAL")!.CaseRevision;
        repository.AppendFormalVersion(new("CASE-FORMAL", currentRevision, 2, replacement,
            new("it-person", "it-account", "IT", "ItFormalRequester"), 1200,
            first.Policy, 4, digest, 1, 1200, "inputs_changed", null,
            "IdentityVerification", "verify_identity", Guid.NewGuid().ToString("D")));

        var old = useCases.ReadVersionReview("CASE-FORMAL", 1)!;
        var current = useCases.ReadVersionReview("CASE-FORMAL", 2)!;
        Assert.False(old.IsCurrent);
        Assert.Equal("formal_version_superseded", old.SafeStopCode);
        Assert.True(current.IsCurrent);
        Assert.Equal(1, current.PreviousVersion);
        Assert.True(current.Differences.Single(item => item.Label == "Template").Changed);
    }

    [Fact]
    public void Public_command_contract_accepts_identifiers_not_caller_owned_actor_objects()
    {
        var inputs = typeof(ISimulationUseCases).GetMethods()
            .Where(method => method.Name is nameof(ISimulationUseCases.VerifyIdentity)
                or nameof(ISimulationUseCases.ApproveExactVersion))
            .SelectMany(method => method.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToArray();
        Assert.DoesNotContain(typeof(SyntheticActor), inputs);
        Assert.DoesNotContain(typeof(SyntheticIdentityDirectory), inputs);
    }

    [Fact]
    public void Approval_is_valid_at_14_59_and_expires_at_exactly_15_00()
    {
        var approved = ApprovedCase();
        var before = BuildUseCases(Paths(), observedUtcMilliseconds: 900500)
            .ReadPolicyApproval("CASE-FORMAL")!;
        var boundary = BuildUseCases(Paths(), observedUtcMilliseconds: 901500)
            .ReadPolicyApproval("CASE-FORMAL")!;
        Assert.True(approved.Succeeded);
        Assert.Equal("Valid", before.ApprovalStatus);
        Assert.Equal(1, before.RemainingSeconds);
        Assert.Equal("Expired", boundary.ApprovalStatus);
        Assert.Equal(0, boundary.RemainingSeconds);
        Assert.Equal("approval_expired", boundary.SafeStopCode);
    }

    [Fact]
    public void Unavailable_and_backward_clock_block_without_changing_approval()
    {
        Assert.True(ApprovedCase().Succeeded);
        var unavailable = BuildUseCases(Paths(), clock: () =>
            throw new InvalidOperationException("test clock unavailable"))
            .ReadPolicyApproval("CASE-FORMAL")!;
        var backward = BuildUseCases(Paths(), observedUtcMilliseconds: 1499)
            .ReadPolicyApproval("CASE-FORMAL")!;
        Assert.Equal("clock_unavailable", unavailable.SafeStopCode);
        Assert.Equal("clock_moved_backward", backward.SafeStopCode);
        Assert.NotNull(Repository().Load("CASE-FORMAL")!.Approvals.Single().ApprovalId);
    }

    [Theory]
    [InlineData("revoked_utc", "revocation_reason_code", "Revoked", "approval_revoked")]
    [InlineData("invalidated_utc", "invalidation_reason_code", "Superseded", "approval_superseded")]
    public void Revoked_or_superseded_approval_is_never_presented_as_authority(
        string timeColumn, string reasonColumn, string status, string code)
    {
        Assert.True(ApprovedCase().Succeeded);
        Execute($"UPDATE approvals SET {timeColumn}=2000,{reasonColumn}='test' WHERE case_id='CASE-FORMAL'");
        var projection = BuildUseCases(Paths()).ReadPolicyApproval("CASE-FORMAL")!;
        Assert.Equal(status, projection.ApprovalStatus);
        Assert.False(projection.DispatchAuthorityAvailable);
        Assert.Equal(code, projection.SafeStopCode);
    }

    [Fact]
    public void Observation_after_expiry_is_read_only_and_preserves_recorded_approval()
    {
        Assert.True(ApprovedCase().Succeeded);
        var revision = Repository().Load("CASE-FORMAL")!.RowRevision;
        var projection = BuildUseCases(Paths(), observedUtcMilliseconds: 999999)
            .ReadPolicyApproval("CASE-FORMAL")!;
        Assert.Equal("Expired", projection.ApprovalStatus);
        Assert.Equal(revision, Repository().Load("CASE-FORMAL")!.RowRevision);
        Assert.Single(Repository().Load("CASE-FORMAL")!.Approvals);
    }

    [Fact]
    public void Observation_after_post_dispatch_expiry_preserves_exact_operation_and_approval()
    {
        var now = 1500L;
        var useCases = BuildUseCases(Paths(), clock: () =>
            DateTimeOffset.FromUnixTimeMilliseconds(now));
        Assert.True(useCases.CreateIntake(new("CASE-FORMAL", "person-1", "site-1",
            "hr-account", [])).Succeeded);
        Assert.True(useCases.ResolveWaitingForIt(new("CASE-FORMAL", 0,
            "subject-account", "it-account", "corrected")).Succeeded);

        var repository = Repository();
        var policy = SimulationPolicySnapshot.CreateClassified("policy-authority", "v1",
            [new("photo-required", SimulationPolicyOptionClassification.Required)]);
        var request = new FormalRequestSnapshot("site-1", "person-1", "subject-account",
            FormalRequestFields.RequiredIntendedUse, "FAKE-DEVICE-001",
            FormalRequestFields.RequiredSlot, new string('a', 64),
            "quarterdeck-fake-authority", "fake-piv-authentication", "v1");
        var digest = SealedRequestBinding.Compute(ToFields(request), policy, 4);
        repository.AppendFormalVersion(new("CASE-FORMAL", 1, 1, request,
            new("it-person", "it-account", "IT", "ItFormalRequester"), 1100,
            new(policy.AuthorityId, policy.Version, policy.ContentDigest,
                [new("photo-required", "Required")]),
            4, digest, null, null, null, null,
            "IdentityVerification", "verify_identity", Guid.NewGuid().ToString("D")));
        var revision = 2L;
        foreach (var gate in repository.LoadGateRequirements("CASE-FORMAL", 1))
        {
            repository.AppendGateObservation(new(Guid.NewGuid().ToString("D"),
                "CASE-FORMAL", revision++, 1, gate.GateCode, "Satisfied", "ready",
                new("it-person", "it-account", "IT", "ItFormalRequester"), 1250,
                "IdentityVerification", "verify_identity", Guid.NewGuid().ToString("D")));
        }

        Assert.True(Approved(useCases).Succeeded);
        var approval = repository.Load("CASE-FORMAL")!.Approvals.Single();
        var started = useCases.StartFakeIssuance(new("CASE-FORMAL",
            useCases.ReadCase("CASE-FORMAL")!.CaseRevision, 1,
            "it-account", "synthetic-request-1"));
        Assert.True(started.Succeeded, started.SafeResultCode);
        var operation = useCases.ReadFakeIssuance("CASE-FORMAL")!.OperationId!;

        now = approval.ExpiresUtc;
        var observed = useCases.ObserveFakeIssuance(new("CASE-FORMAL", operation,
            "it-account"));
        var after = repository.Load("CASE-FORMAL")!;

        Assert.True(observed.Succeeded, observed.SafeResultCode);
        Assert.Equal("result_observation_already_persisted", observed.SafeResultCode);
        Assert.Equal(operation, after.Approvals.Single().ConsumedByOperationId);
        Assert.Equal(approval.ApprovalId, after.Approvals.Single().ApprovalId);
        Assert.Equal(approval.ExpiresUtc, after.Approvals.Single().ExpiresUtc);
    }

    [Fact]
    public void Expired_approval_requires_a_new_record_instead_of_extension()
    {
        Assert.True(ApprovedCase().Succeeded);
        var expiredView = BuildUseCases(Paths(), observedUtcMilliseconds: 901500);
        var before = Repository().Load("CASE-FORMAL")!;
        var replacement = expiredView.ApproveExactVersion(new("CASE-FORMAL",
            before.RowRevision, 1, "hr-account", Fields()));
        var after = Repository().Load("CASE-FORMAL")!;
        Assert.True(replacement.Succeeded);
        Assert.Equal(2, after.Approvals.Count);
        Assert.NotEqual(after.Approvals[0].ApprovalId, after.Approvals[1].ApprovalId);
        Assert.Equal(901500, after.Approvals[0].InvalidatedUtc);
        Assert.Equal("approval_replaced", after.Approvals[0].InvalidationReasonCode);
        Assert.Equal(1801500, after.Approvals[1].ExpiresUtc);
    }

    [Fact]
    public void Unresolved_required_gates_remain_visible_across_policy_migration()
    {
        var useCases = CreateWithFormalVersion();
        var repository = Repository();
        var first = repository.Load("CASE-FORMAL")!.FormalVersions.Single();
        var policy = SimulationPolicySnapshot.CreateClassified("policy-authority", "v2",
            [new("photo-required", SimulationPolicyOptionClassification.Required)]);
        var replacement = first.Request with { PolicyOptionsVersion = "v2" };
        var digest = SealedRequestBinding.Compute(ToFields(replacement), policy, 4);
        var revision = useCases.ReadCase("CASE-FORMAL")!.CaseRevision;
        repository.AppendFormalVersion(new("CASE-FORMAL", revision, 2, replacement,
            new("it-person", "it-account", "IT", "ItFormalRequester"), 1400,
            new(policy.AuthorityId, policy.Version, policy.ContentDigest,
                [new("photo-required", "Required")]),
            4, digest, 1, 1400, "policy_changed",
            new("00000000-0000-0000-0000-000000000901", 1, 2,
                first.Policy.AuthorityId, first.Policy.Version,
                first.Policy.ContentDigest, policy.AuthorityId, policy.Version,
                policy.ContentDigest, new("it-person", "it-account", "IT",
                    "ItFormalRequester"), 1400, "policy_changed"),
            "IdentityVerification", "verify_identity", Guid.NewGuid().ToString("D")));
        var projection = useCases.ReadPolicyApproval("CASE-FORMAL")!;
        Assert.True(projection.PolicyMigrationLocked);
        Assert.All(projection.MandatoryGates, gate => Assert.False(gate.Satisfied));
    }

    [Fact]
    public void Fake_issuance_records_intent_acceptance_and_pending_result_as_distinct_facts()
    {
        var useCases = CreateWithFormalVersion();
        var approved = Approved(useCases);
        Assert.True(approved.Succeeded, approved.SafeResultCode);
        Assert.Equal(SimulationIntentTransition.SourceAction,
            Repository().Load("CASE-FORMAL")!.RequiredNextActionCode);
        var revision = useCases.ReadCase("CASE-FORMAL")!.CaseRevision;
        var started = useCases.StartFakeIssuance(new("CASE-FORMAL", revision, 1,
            "it-account", "synthetic-request-1"));
        Assert.True(started.Succeeded, started.SafeResultCode);
        var projection = useCases.ReadFakeIssuance("CASE-FORMAL")!;
        Assert.Equal("Durably committed", projection.IntentStatus);
        Assert.Equal("Dispatch observed", projection.DispatchStatus);
        Assert.Equal("Pending observation", projection.ResultStatus);
        Assert.False(projection.RetryAllowed);
        Assert.NotNull(projection.OperationId);
        var audit = useCases.ReadAuditTrail("CASE-FORMAL");
        Assert.Contains(audit, item => item.OperationId == projection.OperationId
            && item.ActorPersonId == "it-person"
            && item.ActorAccountId == "it-account"
            && item.ActorRole == "ItIssuer"
            && item.SafeResult == "intent_committed");
    }

    [Fact]
    public void Host_clock_sub_millisecond_ticks_are_canonicalized_before_dispatch()
    {
        var precise = new DateTimeOffset(
            DateTimeOffset.FromUnixTimeMilliseconds(1500).Ticks + 7,
            TimeSpan.Zero);
        var useCases = CreateWithFormalVersion(clock: () => precise);
        Assert.True(Approved(useCases).Succeeded);

        var started = useCases.StartFakeIssuance(new("CASE-FORMAL",
            useCases.ReadCase("CASE-FORMAL")!.CaseRevision, 1,
            "it-account", "synthetic-request-precise-clock"));

        Assert.True(started.Succeeded, started.SafeResultCode);
        Assert.Equal("Pending observation",
            useCases.ReadFakeIssuance("CASE-FORMAL")!.ResultStatus);
    }

    [Fact]
    public void Pending_operation_requires_same_operation_observation_and_blocks_blind_retry()
    {
        var useCases = CreateWithFormalVersion();
        Assert.True(Approved(useCases).Succeeded);
        var revision = useCases.ReadCase("CASE-FORMAL")!.CaseRevision;
        var started = useCases.StartFakeIssuance(new("CASE-FORMAL", revision, 1,
            "it-account", "synthetic-request-1"));
        Assert.True(started.Succeeded, started.SafeResultCode);
        var projection = useCases.ReadFakeIssuance("CASE-FORMAL")!;
        var retry = useCases.StartFakeIssuance(new("CASE-FORMAL",
            useCases.ReadCase("CASE-FORMAL")!.CaseRevision, 1,
            "it-account", "synthetic-request-2"));
        Assert.False(retry.Succeeded);
        Assert.False(useCases.ReadFakeIssuance("CASE-FORMAL")!.RetryAllowed);
        var observed = useCases.ObserveFakeIssuance(new("CASE-FORMAL",
            projection.OperationId!, "it-account"));
        Assert.True(observed.Succeeded, observed.SafeResultCode);
        Assert.Equal("result_observation_already_persisted", observed.SafeResultCode);
    }

    [Fact]
    public void Reconciliation_requires_it_issuer_and_exact_terminal_finding()
    {
        var useCases = CreateWithFormalVersion();
        Assert.True(Approved(useCases).Succeeded);
        var revision = useCases.ReadCase("CASE-FORMAL")!.CaseRevision;
        var started = useCases.StartFakeIssuance(new("CASE-FORMAL", revision, 1,
            "it-account", "synthetic-request-1"));
        Assert.True(started.Succeeded, started.SafeResultCode);
        var operation = useCases.ReadFakeIssuance("CASE-FORMAL")!.OperationId!;
        var finding = "00000000-0000-0000-0000-000000000499";

        var wrongRole = useCases.ReconcileFakeIssuance(new("CASE-FORMAL", finding,
            operation, "hr-account", "exact_terminal_review"));
        var missingEvidence = useCases.ReconcileFakeIssuance(new("CASE-FORMAL", finding,
            operation, "it-account", "exact_terminal_review"));

        Assert.False(wrongRole.Succeeded);
        Assert.Equal("actor_not_authorized", wrongRole.SafeResultCode);
        Assert.False(missingEvidence.Succeeded);
        Assert.Equal("reconciliation_exact_finding_required",
            missingEvidence.SafeResultCode);
        Assert.Equal("Pending observation",
            useCases.ReadFakeIssuance("CASE-FORMAL")!.ResultStatus);
    }

    [Fact]
    public void Evidence_mode_preserves_terminal_fake_result_for_restart_reconciliation()
    {
        var useCases = CreateWithFormalVersion(
            completeFakeAuthorityAtDispatch: true,
            deferResultPersistence: true);
        Assert.True(Approved(useCases).Succeeded);
        var started = useCases.StartFakeIssuance(new("CASE-FORMAL",
            useCases.ReadCase("CASE-FORMAL")!.CaseRevision, 1,
            "it-account", "synthetic-request-1"));
        Assert.True(started.Succeeded, started.SafeResultCode);
        var operation = useCases.ReadFakeIssuance("CASE-FORMAL")!.OperationId!;

        var restarted = BuildUseCases(Paths(),
            completeFakeAuthorityAtDispatch: true,
            deferResultPersistence: true);
        var unresolved = restarted.ReadFakeIssuance("CASE-FORMAL")!;
        Assert.Equal(operation, unresolved.OperationId);
        Assert.NotNull(unresolved.RecoveryFindingId);
        Assert.False(unresolved.RetryAllowed);

        var reconciled = restarted.ReconcileFakeIssuance(new("CASE-FORMAL",
            unresolved.RecoveryFindingId!, operation, "it-account",
            "exact_terminal_review"));
        Assert.True(reconciled.Succeeded, reconciled.SafeResultCode);
        Assert.Equal("Reconciled",
            restarted.ReadFakeIssuance("CASE-FORMAL")!.ResultStatus);
        var completed = restarted.ReadCase("CASE-FORMAL")!;
        Assert.Equal(WorkflowPresentationCondition.SimulationJourneyComplete,
            completed.Condition);
        Assert.Equal("complete", completed.NextAction.SafeStopCode);
        Assert.False(completed.NextAction.ActionAvailable);
    }

    private static SimulationCommandResult Approved(ISimulationUseCases useCases)
    {
        var verified = useCases.VerifyIdentity(new("CASE-FORMAL",
            useCases.ReadCase("CASE-FORMAL")!.CaseRevision, 1, "hr-account"));
        Assert.True(verified.Succeeded, verified.SafeResultCode);
        return useCases.ApproveExactVersion(new("CASE-FORMAL",
            useCases.ReadCase("CASE-FORMAL")!.CaseRevision, 1,
            "hr-account", Fields()));
    }

    private SimulationCommandResult ApprovedCase()
    {
        var useCases = CreateWithFormalVersion();
        var verified = useCases.VerifyIdentity(new("CASE-FORMAL",
            useCases.ReadCase("CASE-FORMAL")!.CaseRevision, 1, "hr-account"));
        Assert.True(verified.Succeeded, verified.SafeResultCode);
        return useCases.ApproveExactVersion(new("CASE-FORMAL",
            useCases.ReadCase("CASE-FORMAL")!.CaseRevision, 1, "hr-account", Fields()));
    }

    private ISimulationUseCases Create()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        var paths = SimulationStorePaths.Create(
            Path.Combine(_root, "workflow", "workflow.db"),
            Path.Combine(_root, "journal", "journal.db"), source);
        return BuildUseCases(paths);
    }

    private ISimulationUseCases CreateWithFormalVersion(
        bool completeFakeAuthorityAtDispatch = false,
        bool deferResultPersistence = false,
        Func<DateTimeOffset>? clock = null)
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        var paths = SimulationStorePaths.Create(
            Path.Combine(_root, "workflow", "workflow.db"),
            Path.Combine(_root, "journal", "journal.db"), source);
        var useCases = BuildUseCases(paths,
            clock: clock,
            completeFakeAuthorityAtDispatch: completeFakeAuthorityAtDispatch,
            deferResultPersistence: deferResultPersistence);
        Assert.True(useCases.CreateIntake(new("CASE-FORMAL", "person-1", "site-1",
            "hr-account", [])).Succeeded);
        Assert.True(useCases.ResolveWaitingForIt(new("CASE-FORMAL", 0,
            "subject-account", "it-account", "corrected")).Succeeded);
        var connections = new SimulationSqliteConnections(paths);
        var repository = new SimulationWorkflowRepository(connections);
        var policy = SimulationPolicySnapshot.CreateClassified("policy-authority", "v1",
            [new("photo-required", SimulationPolicyOptionClassification.Required)]);
        var request = new FormalRequestSnapshot("site-1", "person-1", "subject-account",
            FormalRequestFields.RequiredIntendedUse, "FAKE-DEVICE-001",
            FormalRequestFields.RequiredSlot, new string('a', 64),
            "quarterdeck-fake-authority", "fake-piv-authentication", "v1");
        var digest = SealedRequestBinding.Compute(ToFields(request), policy, 4);
        repository.AppendFormalVersion(new("CASE-FORMAL", 1, 1, request,
            new("it-person", "it-account", "IT", "ItFormalRequester"), 1100,
            new(policy.AuthorityId, policy.Version, policy.ContentDigest,
                [new("photo-required", "Required")]),
            4, digest, null, null, null, null,
            "IdentityVerification", "verify_identity", Guid.NewGuid().ToString("D")));
        var revision = 2L;
        foreach (var gate in repository.LoadGateRequirements("CASE-FORMAL", 1))
        {
            repository.AppendGateObservation(new(Guid.NewGuid().ToString("D"),
                "CASE-FORMAL", revision++, 1, gate.GateCode, "Satisfied", "ready",
                new("it-person", "it-account", "IT", "ItFormalRequester"), 1250,
                "IdentityVerification", "verify_identity", Guid.NewGuid().ToString("D")));
        }
        return useCases;
    }

    private SimulationWorkflowRepository Repository()
    {
        var paths = SimulationStorePaths.Create(
            Path.Combine(_root, "workflow", "workflow.db"),
            Path.Combine(_root, "journal", "journal.db"),
            Path.Combine(_root, "source"));
        return new(new SimulationSqliteConnections(paths));
    }

    private SimulationStorePaths Paths() => SimulationStorePaths.Create(
        Path.Combine(_root, "workflow", "workflow.db"),
        Path.Combine(_root, "journal", "journal.db"),
        Path.Combine(_root, "source"));

    private void Execute(string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(_root, "workflow", "workflow.db"), Pooling = false }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private long Scalar(string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(_root, "workflow", "workflow.db"), Pooling = false }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static ISimulationUseCases BuildUseCases(SimulationStorePaths paths,
        long observedUtcMilliseconds = 1500, Func<DateTimeOffset>? clock = null,
        bool completeFakeAuthorityAtDispatch = false,
        bool deferResultPersistence = false) =>
        SimulationUseCases.Create(paths, "application-test",
        [
            Binding("hr-person", "hr-account", "HR", "site-1",
                SyntheticWorkflowRole.HrIntake, SyntheticWorkflowRole.HrVerifier,
                SyntheticWorkflowRole.HrApprover),
            Binding("it-person", "it-account", "IT", "site-1",
                SyntheticWorkflowRole.ItFormalRequester, SyntheticWorkflowRole.ItIssuer),
            Binding("person-1", "subject-account", "Subject", "site-1"),
            Binding("person-2", "other-account", "Other", "site-2"),
            Binding("lookalike", "lookalike-account", "HR", "site-1")
        ], ["FAKE-DEVICE-001"], ["quarterdeck-fake-authority"],
            ["subject-account"], clock ?? (() =>
                DateTimeOffset.FromUnixTimeMilliseconds(observedUtcMilliseconds)),
            completeFakeAuthorityAtDispatch, deferResultPersistence);

    private static FormalRequestFieldInput Fields() => new("site-1", "person-1",
        "subject-account", FormalRequestFields.RequiredIntendedUse, "FAKE-DEVICE-001",
        FormalRequestFields.RequiredSlot, new string('a', 64),
        "quarterdeck-fake-authority", "fake-piv-authentication", "v1");

    private static FormalRequestFields ToFields(FormalRequestSnapshot value) => new(
        new(value.SiteId), new(value.PersonId), new(value.AccountId), value.IntendedUse,
        value.FakeDeviceId, value.Slot, value.SyntheticPublicRequestDigest,
        value.FakeAuthority, value.Template, value.PolicyOptionsVersion);

    private static SyntheticIdentityBinding Binding(string person, string account,
        string label, string site, params SyntheticWorkflowRole[] roles) =>
        new(new(person), new(account), label, new(site), roles.ToHashSet());

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class RecordingUseCases : ISimulationUseCases
    {
        public SimulationCommandResult ReturnToHr(HrCorrectionRequest request) => new(false,"not_used",request.CaseId);
        public SimulationCommandResult ResubmitToIt(HrCorrectionRequest request) => new(false,"not_used",request.CaseId);
        public IReadOnlyList<HrCorrectionView> ReadHrCorrections(string caseId) => [];
        public IReadOnlyList<NameChangeView> ReadNameChanges() => [];
        public SimulationCommandResult RequestNameChange(NameChangeRequest request) => new(false,"not_used",request.CaseId);
        public SimulationCommandResult CompleteNameChange(string requestId,string actorAccountId,string comment) => new(false,"not_used",string.Empty);
        public ResolveWaitingForItRequest? Request { get; private set; }
        public VerifyIdentityRequest? Verification { get; private set; }
        public ApproveExactVersionRequest? Approval { get; private set; }
        public StartFakeIssuanceRequest? StartIssuance { get; private set; }
        public ObserveFakeIssuanceRequest? ObserveIssuance { get; private set; }
        public ReconcileFakeIssuanceRequest? ReconcileIssuance { get; private set; }
        public IReadOnlyList<DepartmentOption> ReadDepartments() => [];
        public IReadOnlyList<SimulationWorkflowProjection> ReadWorkQueue(WorkQueueFilter filter) => [];
        public SimulationWorkflowProjection? ReadCase(string caseId) => null;
        public FormalVersionReviewProjection? ReadVersionReview(string caseId, long version) => null;
        public PolicyApprovalProjection? ReadPolicyApproval(string caseId) => null;
        public FakeIssuanceProjection? ReadFakeIssuance(string caseId) => null;
        public IReadOnlyList<AuditEventProjection> ReadAuditTrail(string caseId) => [];
        public IReadOnlyList<DeniedCommandProjection> ReadDeniedCommands(string caseId) => [];
        public SimulationCounterProjection ReadSimulationCounters() => new(0);
        public SimulationCommandResult CreateIntake(CreateSyntheticIntakeRequest request) =>
            new(false, "not_used", request.CaseId);
        public SimulationCommandResult RegisterSimulationAccount(RegisterSimulationAccountRequest request) =>
            new(false, "not_used", request.CaseId);
        public SimulationCommandResult ResolveWaitingForIt(ResolveWaitingForItRequest request)
        {
            Request = request;
            return new(false, "case_revision_stale", request.CaseId);
        }
        public SimulationCommandResult SealFormalRequest(SealFormalRequest request) =>
            new(false, "not_used", request.CaseId);
        public SimulationCommandResult ObserveMandatoryGate(ObserveMandatoryGateRequest request) =>
            new(false, "not_used", request.CaseId);
        public SimulationCommandResult VerifyIdentity(VerifyIdentityRequest request)
        {
            Verification = request;
            return new(false, "not_used", request.CaseId);
        }
        public SimulationCommandResult ApproveExactVersion(ApproveExactVersionRequest request)
        {
            Approval = request;
            return new(false, "not_used", request.CaseId);
        }
        public SimulationCommandResult StartFakeIssuance(StartFakeIssuanceRequest request)
        {
            StartIssuance = request;
            return new(false, "not_used", request.CaseId);
        }
        public SimulationCommandResult ObserveFakeIssuance(ObserveFakeIssuanceRequest request)
        {
            ObserveIssuance = request;
            return new(false, "not_used", request.CaseId);
        }
        public SimulationCommandResult ReconcileFakeIssuance(ReconcileFakeIssuanceRequest request)
        {
            ReconcileIssuance = request;
            return new(false, "not_used", request.CaseId);
        }
    }
}
