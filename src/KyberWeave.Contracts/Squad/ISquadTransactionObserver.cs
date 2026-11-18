namespace KyberWeave.Core.Squad.Deployment;

/// <summary>Receives transaction checkpoints after their corresponding filesystem mutation.</summary>
public interface ISquadTransactionObserver
{
    void AfterStep(SquadTransactionStep transactionStep);
}
