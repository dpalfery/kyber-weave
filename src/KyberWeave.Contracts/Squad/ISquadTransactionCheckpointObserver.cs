namespace KyberWeave.Core.Squad.Deployment;

public interface ISquadTransactionCheckpointObserver : ISquadTransactionObserver
{
    void AfterCheckpoint(SquadTransactionCheckpoint checkpoint);
}
