namespace KyberWeave.Core.Squad.Deployment;

/// <summary>A durable point in the ordered Squad deployment transaction.</summary>
public enum SquadTransactionStepKind
{
    IntentWritten,
    FileStaged,
    FileBackedUp,
    FileApplied,
    LockApplied,
    ReceiptApplied
}

/// <summary>An observable transaction step, numbered from one in filesystem order.</summary>
public sealed record SquadTransactionStep(
    int Sequence,
    SquadTransactionStepKind Kind,
    string? RelativePath = null);

/// <summary>Receives transaction checkpoints after their corresponding filesystem mutation.</summary>
public interface ISquadTransactionObserver
{
    void AfterStep(SquadTransactionStep transactionStep);
}

public enum SquadTransactionCheckpointKind
{
    Prepared,
    ActiveTransitionWritten,
    OriginalClaimed,
    AfterImagePublished
}

public sealed record SquadTransactionCheckpoint(
    int Sequence,
    SquadTransactionCheckpointKind Kind,
    string? RelativePath = null);

public interface ISquadTransactionCheckpointObserver : ISquadTransactionObserver
{
    void AfterCheckpoint(SquadTransactionCheckpoint checkpoint);
}
