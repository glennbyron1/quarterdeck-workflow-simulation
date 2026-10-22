using System.Reflection;
using Quarterdeck.Presentation;

namespace Quarterdeck.Presentation.Tests;

public sealed class SimulationWorkflowProjectionTests
{
    [Theory]
    [InlineData("none", "attempted", "none", AuditStateKind.Attempted)]
    [InlineData("denied", "not-recorded", "none", AuditStateKind.Denied)]
    [InlineData("authorized", "failed", "store_unavailable", AuditStateKind.Failed)]
    [InlineData("authorized", "interrupted", "none", AuditStateKind.Interrupted)]
    [InlineData("authorized", "recorded", "result_pending", AuditStateKind.Pending)]
    [InlineData("authorized", "recorded", "result_unknown", AuditStateKind.Unknown)]
    [InlineData("authorized", "recorded", "recorded", AuditStateKind.Successful)]
    public void Every_audit_state_has_distinct_semantics_and_visual_token(
        string decision, string outcome, string result, AuditStateKind expected)
    {
        var projection = AuditStatePresentationMapper.Map(decision, outcome, result);
        Assert.Equal(expected, projection.Kind);
        Assert.False(string.IsNullOrWhiteSpace(projection.Label));
        Assert.StartsWith("audit-", projection.CssClass, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(projection.SafeCode));
    }

    [Fact]
    public void Audit_state_inventory_has_unique_labels_styles_and_codes()
    {
        var values = new[]
        {
            AuditStatePresentationMapper.Map("none", "attempted", "none"),
            AuditStatePresentationMapper.Map("denied", "none", "none"),
            AuditStatePresentationMapper.Map("authorized", "failed", "none"),
            AuditStatePresentationMapper.Map("authorized", "interrupted", "none"),
            AuditStatePresentationMapper.Map("authorized", "none", "pending"),
            AuditStatePresentationMapper.Map("authorized", "none", "unknown"),
            AuditStatePresentationMapper.Map("authorized", "recorded", "recorded")
        };
        Assert.Equal(Enum.GetValues<AuditStateKind>().Length, values.Length);
        Assert.Equal(values.Length, values.Select(item => item.Kind).Distinct().Count());
        Assert.Equal(values.Length, values.Select(item => item.Label).Distinct().Count());
        Assert.Equal(values.Length, values.Select(item => item.CssClass).Distinct().Count());
        Assert.Equal(values.Length, values.Select(item => item.SafeCode).Distinct().Count());
    }

    public static IEnumerable<object[]> EveryCondition() =>
        Enum.GetValues<WorkflowPresentationCondition>().Select(value => new object[] { value });

    [Theory]
    [MemberData(nameof(EveryCondition))]
    public void Every_condition_has_one_truthful_presentation(WorkflowPresentationCondition condition)
    {
        var projected = SimulationWorkflowProjectionMapper.Map(Input(condition));

        Assert.Equal(condition, projected.Condition);
        Assert.False(string.IsNullOrWhiteSpace(projected.Headline));
        Assert.False(string.IsNullOrWhiteSpace(projected.NextAction.Action));
        Assert.False(string.IsNullOrWhiteSpace(projected.NextAction.SafeStopCode));
        Assert.Single(projected.Timeline);
    }

    [Fact]
    public void Supported_condition_inventory_is_exact_and_unique()
    {
        var expected = Enum.GetValues<WorkflowPresentationCondition>();
        Assert.Equal(expected, SimulationWorkflowProjectionMapper.SupportedConditions);
        Assert.Equal(expected.Length, SimulationWorkflowProjectionMapper.SupportedConditions.Distinct().Count());
    }

    [Theory]
    [InlineData(WorkflowPresentationCondition.FakeIssuancePending, "Observe the same fake request; do not resubmit.")]
    [InlineData(WorkflowPresentationCondition.OutcomeUnresolved, "Perform read-only observation and exact-evidence reconciliation.")]
    [InlineData(WorkflowPresentationCondition.EvidenceMismatch, "Do not retry; investigate the exact operation binding.")]
    public void Uncertain_states_never_offer_blind_retry(
        WorkflowPresentationCondition condition,
        string expectedAction)
    {
        var projected = SimulationWorkflowProjectionMapper.Map(Input(condition));

        Assert.Equal(WorkflowQueueGroup.NeedsAttention, projected.QueueGroup);
        Assert.Equal(expectedAction, projected.NextAction.Action);
        Assert.DoesNotContain("Start fake issuance", projected.NextAction.Action,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Completion_language_excludes_live_and_physical_claims()
    {
        var projected = SimulationWorkflowProjectionMapper.Map(
            Input(WorkflowPresentationCondition.SimulationJourneyComplete));

        Assert.Equal(WorkflowNextRole.None, projected.NextAction.Role);
        Assert.False(projected.NextAction.ActionAvailable);
        Assert.Contains("does not prove physical delivery or Windows logon", projected.NextAction.Action);
    }

    [Fact]
    public void Projection_copies_the_timeline_instead_of_retaining_caller_storage()
    {
        var timeline = new List<TimelineProjection> { Timeline() };
        var input = Input(WorkflowPresentationCondition.WaitingForIt) with { Timeline = timeline };

        var projected = SimulationWorkflowProjectionMapper.Map(input);
        timeline.Clear();

        Assert.Single(projected.Timeline);
    }

    [Fact]
    public void Presentation_assembly_has_no_persistence_or_sqlite_dependency()
    {
        var references = typeof(SimulationWorkflowProjectionMapper).Assembly
            .GetReferencedAssemblies().Select(item => item.Name).ToArray();

        Assert.DoesNotContain("Quarterdeck.Persistence", references);
        Assert.DoesNotContain("Microsoft.Data.Sqlite", references);
        Assert.DoesNotContain(typeof(SimulationWorkflowProjectionMapper).GetMethods(BindingFlags.Public | BindingFlags.Static),
            method => method.Name.StartsWith("Create", StringComparison.Ordinal)
                || method.Name.StartsWith("Update", StringComparison.Ordinal)
                || method.Name.StartsWith("Delete", StringComparison.Ordinal)
                || method.Name.StartsWith("Approve", StringComparison.Ordinal)
                || method.Name.StartsWith("Dispatch", StringComparison.Ordinal));
    }

    [Fact]
    public void Queue_is_deterministic_and_attention_first()
    {
        var items = new[]
        {
            SimulationWorkflowProjectionMapper.Map(Input(WorkflowPresentationCondition.AwaitingExactApproval) with { CaseId = "CASE-020" }),
            SimulationWorkflowProjectionMapper.Map(Input(WorkflowPresentationCondition.OutcomeUnresolved) with { CaseId = "CASE-030" }),
            SimulationWorkflowProjectionMapper.Map(Input(WorkflowPresentationCondition.OutcomeUnresolved) with { CaseId = "CASE-010" })
        };

        Assert.Equal(new[] { "CASE-010", "CASE-030", "CASE-020" },
            SimulationWorkQueue.Build(items).Select(item => item.CaseId));
    }

    [Fact]
    public void Queue_filters_do_not_change_case_authority_or_action()
    {
        var item = SimulationWorkflowProjectionMapper.Map(
            Input(WorkflowPresentationCondition.AwaitingExactApproval));

        var filtered = Assert.Single(SimulationWorkQueue.Build(new[] { item },
            new(WorkflowNextRole.HrApprover, "SITE-A", WorkflowPresentationCondition.AwaitingExactApproval)));

        Assert.Same(item, filtered);
        Assert.Equal("exact_approval_required", filtered.NextAction.SafeStopCode);
    }

    private static SimulationWorkflowProjectionInput Input(WorkflowPresentationCondition condition) => new(
        "CASE-001", "TEST Person 001", "SITE-A", 7, condition,
        new(1, "Current", PresentationFactSource.Recorded,
            new[] { new PresentationFact("Site ID", "SITE-A", PresentationFactSource.Recorded) }),
        new("Valid", "approval-001", DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddMinutes(15), DateTimeOffset.UnixEpoch,
            PresentationFactSource.Recorded),
        new("Not started", null, 0, 0, null, PresentationFactSource.Recorded),
        new[] { Timeline() });

    private static TimelineProjection Timeline() => new(
        1, DateTimeOffset.UnixEpoch, "Intake recorded", "TEST-HR-001",
        "HR requester", "Recorded", PresentationFactSource.Recorded);
}
