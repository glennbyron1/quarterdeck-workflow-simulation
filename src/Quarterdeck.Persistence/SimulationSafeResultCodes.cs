namespace Quarterdeck.Persistence;

/// <summary>
/// Closed diagnostic vocabulary for persistence boundaries. Exceptions and their
/// messages are never returned to callers or written as evidence.
/// </summary>
internal static class SimulationSafeResultCodes
{
    internal const string AuditCorrectionRecorded = "audit_correction_recorded";
    internal const string AuditCorrectionUnauthorized = "audit_correction_unauthorized";
    internal const string AuditCorrectionInvalid = "audit_correction_invalid";
    internal const string AuditCorrectionTargetMissing = "audit_correction_target_missing";
    internal const string AuditCorrectionStoreUnavailable = "audit_correction_store_unavailable";
    internal const string ReconciliationUnknown = "reconciliation_unknown";
    internal const string RecoveryDiscoveryUnknown = "recovery_discovery_unknown";
    internal const string ResultPersistenceUnknown = "result_persistence_unknown";
    internal const string RecoveryGateUnavailable = "recovery_gate_unavailable";
    internal const string IntentDispatchAuthorityUnavailable = "intent_committed_dispatch_authority_unavailable";
    internal const string AuthorityJournalUnavailable = "authority_journal_unavailable";
    internal const string AuthorityObservationUnreadable = "authority_observation_unreadable";

    internal static string Map(Exception exception, SimulationDiagnosticBoundary boundary)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return boundary switch
        {
            SimulationDiagnosticBoundary.AuditCorrection => AuditCorrectionStoreUnavailable,
            SimulationDiagnosticBoundary.Reconciliation => ReconciliationUnknown,
            SimulationDiagnosticBoundary.RecoveryDiscovery => RecoveryDiscoveryUnknown,
            SimulationDiagnosticBoundary.ResultPersistence => ResultPersistenceUnknown,
            _ => "simulation_operation_unknown"
        };
    }
}

internal enum SimulationDiagnosticBoundary
{
    AuditCorrection,
    Reconciliation,
    RecoveryDiscovery,
    ResultPersistence
}
