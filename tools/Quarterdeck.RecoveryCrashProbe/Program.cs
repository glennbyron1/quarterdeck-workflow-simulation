using Quarterdeck.Persistence;

if (args.Length != 7)
    return 64;

var paths = SimulationStorePaths.Create(args[0], args[1], args[2]);
var connections = new SimulationSqliteConnections(paths);
var journal = new SimulationAuthorityJournalRepository(connections,
    new AuthorityCallCounters());
var recovery = new SimulationRecoveryCoordinator(connections, journal);
recovery.PersistObservation(new(args[3], args[4], args[5],
    long.Parse(args[6], System.Globalization.CultureInfo.InvariantCulture)),
    new FailFastAt(WorkflowResultFaultPoint.BeforeCommit));
return 65;

file sealed class FailFastAt(WorkflowResultFaultPoint target) : IWorkflowResultFault
{
    public void Observe(WorkflowResultFaultPoint point)
    {
        if (point == target)
            Environment.FailFast("quarterdeck_synthetic_crash_probe");
    }
}
