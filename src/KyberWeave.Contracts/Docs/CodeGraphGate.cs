namespace KyberWeave.Contracts.Docs;

/// <summary>What one CodeGraph check means for the tool call that asked for it.</summary>
/// <param name="Failure">The block a tool returns instead of its own answer, or null.</param>
/// <param name="Note">One line the answer carries above its body, or null.</param>
public sealed record CodeGraphGate(string? Failure, string? Note)
{
    /// <summary>CodeGraph is usable, and the call proceeds unchanged.</summary>
    public static CodeGraphGate Clear { get; } = new(null, null);

    /// <summary>The call must report the failure instead of answering.</summary>
    public static CodeGraphGate Blocking(string failure) => new(failure, null);

    /// <summary>The call answers, and says what happened in one line first.</summary>
    public static CodeGraphGate Advisory(string note) => new(null, note);
}
