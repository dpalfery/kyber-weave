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

public enum SquadTransactionCheckpointKind
{
    Prepared,
    ActiveTransitionWritten,
    OriginalClaimed,
    AfterImagePublished,

    /// <summary>The deployment has committed: its receipt is published and only cleanup is left.</summary>
    Committed
}

public sealed record SquadTransactionCheckpoint(
    int Sequence,
    SquadTransactionCheckpointKind Kind,
    string? RelativePath = null);
