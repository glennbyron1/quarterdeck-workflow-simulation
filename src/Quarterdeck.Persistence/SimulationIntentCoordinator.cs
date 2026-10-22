namespace Quarterdeck.Persistence;

/// <summary>
/// Narrow durable-intent gate. It deliberately cannot execute a continuation;
/// Chunk 4 supplies the independently journaled acceptance guard and effect.
/// </summary>
internal sealed class SimulationIntentCoordinator
{
    private readonly SimulationWorkflowRepository _repository;
    private readonly SimulationRecoveryCoordinator _recovery;

    internal SimulationIntentCoordinator(SimulationWorkflowRepository repository,
        SimulationRecoveryCoordinator recovery)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
        if (!ReferenceEquals(_repository.StoreSetToken, _recovery.StoreSetToken))
            throw new ArgumentException("recovery_store_set_mismatch", nameof(recovery));
    }

    internal IntentCommitResult Commit(
        CommitIntentCommand command,
        IIntentCommitFault? fault = null)
    {
        var result = _repository.CommitIntent(command, fault);
        if (result.IsDurablyCommitted && result.Receipt is { } receipt)
        {
            var fields = command.Admission.Fields;
            try
            {
                if (_recovery.IsDispatchBlocked(fields.SiteId.Value,
                    fields.AccountId.Value, fields.IntendedUse, fields.FakeDeviceId,
                    fields.Slot))
                {
                    return new(IntentCommitStatus.CommittedDispatchBlocked,
                        "recovery_finding_blocks_dispatch", receipt);
                }
            }
            catch
            {
                return new(IntentCommitStatus.CommittedDispatchBlocked,
                    SimulationSafeResultCodes.RecoveryGateUnavailable, receipt);
            }
            try
            {
                command.Admission.Revalidate();
            }
            catch
            {
                return new(IntentCommitStatus.CommittedDispatchBlocked,
                    SimulationSafeResultCodes.IntentDispatchAuthorityUnavailable, receipt);
            }
        }
        return result;
    }
}
