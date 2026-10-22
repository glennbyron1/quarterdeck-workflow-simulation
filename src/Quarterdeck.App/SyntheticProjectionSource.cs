using Quarterdeck.Presentation;

namespace Quarterdeck.App;

public interface ISimulationProjectionSource
{
    IReadOnlyList<SimulationWorkflowProjection> ReadWorkQueue(WorkQueueFilter filter);
    SimulationWorkflowProjection? ReadCase(string caseId);
}

public sealed class SyntheticProjectionSource : ISimulationProjectionSource
{
    private static readonly DateTimeOffset Seed = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
    private readonly IReadOnlyList<SimulationWorkflowProjection> _cases =
        Array.AsReadOnly(new[]
        {
            Create("SIM-CASE-003", "TEST Casey River", "SITE-B",
                WorkflowPresentationCondition.OutcomeUnresolved, Seed.AddMinutes(-19)),
            Create("SIM-CASE-001", "TEST Morgan Harbor", "SITE-A",
                WorkflowPresentationCondition.WaitingForIt, Seed.AddMinutes(-12)),
            Create("SIM-CASE-002", "TEST Avery Beacon", "SITE-A",
                WorkflowPresentationCondition.AwaitingExactApproval, Seed.AddMinutes(-7)),
            Create("SIM-CASE-004", "TEST Riley Compass", "SITE-B",
                WorkflowPresentationCondition.ReadyForSimulatedIssuance, Seed.AddMinutes(-3))
        });

    public IReadOnlyList<SimulationWorkflowProjection> ReadWorkQueue(WorkQueueFilter filter) =>
        SimulationWorkQueue.Build(_cases, filter);

    public SimulationWorkflowProjection? ReadCase(string caseId) =>
        _cases.SingleOrDefault(item => string.Equals(item.CaseId, caseId, StringComparison.Ordinal));

    private static SimulationWorkflowProjection Create(
        string caseId,
        string displayLabel,
        string siteId,
        WorkflowPresentationCondition condition,
        DateTimeOffset occurredAt) => SimulationWorkflowProjectionMapper.Map(new(
            caseId, displayLabel, siteId, 0, condition,
            new(1, "Current", PresentationFactSource.Recorded, new PresentationFact[]
            {
                new("Site ID", siteId, PresentationFactSource.Recorded),
                new("Person ID", $"TEST-PERSON-{caseId[^3..]}", PresentationFactSource.Recorded),
                new("Account ID", $"TEST-ACCOUNT-{caseId[^3..]}", PresentationFactSource.Recorded),
                new("Intended use", "PIV-authentication-simulation", PresentationFactSource.Recorded),
                new("Fake device ID", $"FAKE-DEVICE-{caseId[^3..]}", PresentationFactSource.Recorded),
                new("Slot", "9a", PresentationFactSource.Recorded),
                new("Synthetic public-request digest", new string(caseId[^1], 64), PresentationFactSource.Recorded),
                new("Fake authority", "quarterdeck-fake-authority", PresentationFactSource.Recorded),
                new("Template", "fake-piv-authentication", PresentationFactSource.Recorded),
                new("Policy/options version", "simulation-policy-v1", PresentationFactSource.Recorded)
            }),
            new("Not approved", null, null, null, Seed, PresentationFactSource.Recorded),
            new("Not started", null, 0, 0, null, PresentationFactSource.Recorded),
            new TimelineProjection[]
            {
                new(1, occurredAt, "Intake recorded", "TEST-HR-001", "HR requester",
                    "Recorded", PresentationFactSource.Recorded)
            }));
}
