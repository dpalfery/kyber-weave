using System.Text.Json;
using KyberWeave.Core.Arbiter.Rules;

namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>
/// The decision log (<c>artifacts/arbiter/decisions.jsonl</c>, schema
/// <c>kyber-arbiter.decision/v1</c>): every decision the Arbiter records, and
/// the <c>REPEAT</c> query the escalation envelope reads (§1.10).
/// </summary>
/// <remarks>
/// Append-only, like the ledger: the hook computes <c>REPEAT</c> from the
/// decisions already in the log, so the conductor keeps no count and a Draft
/// amendment — which changes the plan digest — resets it (R8). The arbiter
/// directory is passed in because the hook resolves the repository root with
/// <c>git rev-parse --show-toplevel</c> from the payload's <c>cwd</c>.
/// </remarks>
public sealed class DecisionLog
{
    /// <summary>The decision log file name, relative to the arbiter directory.</summary>
    public const string FileName = "decisions.jsonl";

    private readonly string _directory;

    /// <summary>Creates a decision log over <paramref name="arbiterDirectory"/>.</summary>
    public DecisionLog(string arbiterDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(arbiterDirectory);
        _directory = arbiterDirectory;
    }

    /// <summary>The directory holding the ledger, the decision log and the lock.</summary>
    public string DirectoryPath => _directory;

    /// <summary>The decision log file path.</summary>
    public string FilePath => Path.Combine(_directory, FileName);

    /// <summary>The exclusive append lock path (shared with the ledger).</summary>
    public string LockFilePath => Path.Combine(_directory, ".lock");

    /// <summary>Appends one decision as one complete JSON line under the exclusive lock.</summary>
    /// <exception cref="IOException">Thrown when the lock is not acquired within 500 ms (fail closed).</exception>
    public async Task AppendAsync(ArbiterDecisionRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await ArbiterLogFiles.AppendLineAsync(_directory, FileName, ArbiterJson.Serialize(record), cancellationToken);
    }

    /// <summary>Reads every complete decision, in append order. Takes no lock.</summary>
    public IReadOnlyList<ArbiterDecisionRecord> ReadAll() =>
        [.. ArbiterLogFiles.ReadCompleteLines(FilePath)
            .Select(line => JsonSerializer.Deserialize<ArbiterDecisionRecord>(line, ArbiterJson.Options))
            .Select(d => d!)];

    /// <summary>
    /// The <c>REPEAT</c> count: one plus the number of earlier escalation
    /// decisions with the same plan file, plan digest and task that share
    /// <paramref name="ruleId"/>. The current decision is not in the log yet
    /// when the hook calls this, so "earlier" is every matching decision read.
    /// </summary>
    public int Repeat(string ruleId, string? planFile, string? planDigest, string? task)
    {
        ArgumentNullException.ThrowIfNull(ruleId);
        return 1 + ReadAll().Count(decision =>
            string.Equals(decision.Outcome, RuleEffects.Escalate, StringComparison.Ordinal)
            && decision.Rules.Any(rule => string.Equals(rule.Id, ruleId, StringComparison.Ordinal))
            && string.Equals(decision.PlanFile, planFile, StringComparison.Ordinal)
            && string.Equals(decision.PlanDigest, planDigest, StringComparison.Ordinal)
            && string.Equals(decision.Task, task, StringComparison.Ordinal));
    }
}
