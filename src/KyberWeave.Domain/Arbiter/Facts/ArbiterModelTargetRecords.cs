namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>One file a task touches, tagged with its extension and whether it matches the test-file patterns.</summary>
/// <param name="Extension">The file extension, including its dot (<c>.cs</c>), or empty when the file has none.</param>
/// <param name="Kind"><c>test</c> when a test-file pattern matches the path, otherwise <c>source</c>.</param>
public sealed record ArbiterFileKind(string Extension, string Kind);

/// <summary>The most recent earlier attempt at the same plan task, as the ledger and decision log record it.</summary>
/// <param name="Result"><c>pass</c> or <c>fail</c>, from the attempt's task-review marker.</param>
/// <param name="Profile">The profile the attempt ran on, when one was recorded.</param>
/// <param name="Token">The model token the attempt ran on, when one was recorded.</param>
public sealed record ArbiterPriorAttempt(string Result, string? Profile, string? Token);
