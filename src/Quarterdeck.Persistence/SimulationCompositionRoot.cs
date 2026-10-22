using Quarterdeck.Core.Simulation;

namespace Quarterdeck.Persistence;

/// <summary>
/// Phase 1's complete composition boundary. Only the fake authority journal can
/// be constructed; rejected adapter configuration is evaluated before schema or
/// repository creation and has no fallback path.
/// </summary>
internal static class SimulationCompositionRoot
{
    internal static SimulationRuntime Compose(
        string? configuredAdapter,
        SimulationStorePaths paths,
        string sourceRevision,
        SyntheticDispatchAdmissionAuthority? dispatchAuthority = null,
        bool completeFakeAuthorityAtDispatch = false)
    {
        FakeAdapterTokenPolicy.Validate(configuredAdapter);
        ArgumentNullException.ThrowIfNull(paths);
        if (string.IsNullOrWhiteSpace(sourceRevision) || sourceRevision.Length > 80)
            throw new ArgumentException("simulation_source_revision_invalid",
                nameof(sourceRevision));

        var connections = new SimulationSqliteConnections(paths);
        new SimulationSchemaInitializer(connections).Initialize(sourceRevision);
        var workflow = new SimulationWorkflowRepository(connections, dispatchAuthority);
        var counters = new AuthorityCallCounters();
        var journal = new SimulationAuthorityJournalRepository(connections, counters,
            completeFakeAuthorityAtDispatch);
        var recovery = new SimulationRecoveryCoordinator(connections, journal);
        return new SimulationRuntime(
            FakeAdapterTokenPolicy.AdapterName,
            FakeAdapterTokenPolicy.DisplayLabel,
            workflow,
            recovery,
            new SimulationIntentCoordinator(workflow, recovery), counters);
    }
}

internal sealed record SimulationRuntime(
    string AdapterName,
    string DisplayLabel,
    SimulationWorkflowRepository Workflow,
    SimulationRecoveryCoordinator Recovery,
    SimulationIntentCoordinator Intent,
    AuthorityCallCounters Counters);
