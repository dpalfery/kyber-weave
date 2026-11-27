using KyberWeave.Core.Configuration;
using KyberWeave.Core.RunStart;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Contracts.Services;

/// <summary>How a run start ended; each value maps to one exit code the caller branches on.</summary>
public enum RunStartStatus
{
    /// <summary>The branch exists and the run may continue.</summary>
    Started,

    /// <summary>The caller supplied input the step cannot act on.</summary>
    RefusedInput,

    /// <summary>A tool this step depends on failed, or the repository has not answered onboarding.</summary>
    ToolFailure,

    /// <summary>The CodeGraph binary is not installed.</summary>
    CodeGraphMissing
}

/// <summary>What a run-start message is, and so how the command prints it.</summary>
public enum RunStartMessageKind
{
    /// <summary>The reason the step stopped.</summary>
    Error,

    /// <summary>The way to put it right.</summary>
    Remedy,

    /// <summary>A further line under the remedy.</summary>
    Note,

    /// <summary>Something the step continued past.</summary>
    Warning
}

/// <summary>One line the step has to tell the operator.</summary>
/// <param name="Kind">What the line is.</param>
/// <param name="Text">The unformatted text.</param>
/// <param name="StandardErrorWhenJson">Whether <c>--json</c> sends the line to standard error so standard output stays one document.</param>
public sealed record RunStartMessage(RunStartMessageKind Kind, string Text, bool StandardErrorWhenJson = false);

/// <summary>What a started run reports: where the branch is, what it was cut from and how the checkout compares with the server.</summary>
/// <param name="BaseBranch">The base branch's name.</param>
/// <param name="StartRef">The ref the branch was cut from.</param>
/// <param name="Branch">The branch the work belongs on.</param>
/// <param name="WorktreePath">The worktree the branch lives in, or null for in place.</param>
/// <param name="Location">Where the branch was placed.</param>
/// <param name="BaseRevision">The commit the base resolves to.</param>
/// <param name="ResumeTarget">The branch that was resumed, or null for a new branch.</param>
/// <param name="IndexLabel">The index state: ready, created, not-governed or warning.</param>
/// <param name="Completed">The tasks the resumed branch already finished.</param>
/// <param name="CompletedWarning">Why the finished tasks could not be read, or null.</param>
/// <param name="HeadVersion">The version <c>HEAD</c> describes as.</param>
/// <param name="BaseVersion">The version the base describes as.</param>
/// <param name="OriginAddress">The address of <c>origin</c>.</param>
/// <param name="AlignmentRevision">The commit the alignment was measured from.</param>
/// <param name="HeadRevision">The commit <c>HEAD</c> resolved to before anything was switched.</param>
/// <param name="Ahead">How many commits the checkout is ahead of the base.</param>
/// <param name="Behind">How many commits the checkout is behind the base.</param>
/// <param name="IndexTree">The tree the index was prepared in.</param>
public sealed record RunStartFacts(
    string BaseBranch,
    string StartRef,
    string Branch,
    string? WorktreePath,
    RunStartLocation Location,
    string? BaseRevision,
    string? ResumeTarget,
    string IndexLabel,
    IReadOnlyList<RunStartCompletedTask> Completed,
    string? CompletedWarning,
    string HeadVersion,
    string BaseVersion,
    string? OriginAddress,
    string? AlignmentRevision,
    string? HeadRevision,
    int? Ahead,
    int? Behind,
    string IndexTree);

/// <summary>Everything one run start needs from the command line and the loaded configuration.</summary>
/// <param name="RepositoryRoot">The repository the run starts in.</param>
/// <param name="Branch">The branch name the work gets.</param>
/// <param name="Resume">The branch to resume, or null to start a new one.</param>
/// <param name="Config">The loaded repository configuration.</param>
/// <param name="ConfigPath">The configuration file that was read, or null when the repository has none.</param>
/// <param name="IndexExecutor">Runs the CodeGraph child that prepares the index; null for the bounded production executor.</param>
/// <param name="Autonomy">The autonomy the run resolved, recorded with the run; null when none was set.</param>
public sealed record RunStartRequest(
    string RepositoryRoot,
    string Branch,
    string? Resume,
    KyberWeaveConfig Config,
    string? ConfigPath,
    IProcessExecutor? IndexExecutor,
    string? Autonomy = null);

/// <summary>How one run start ended.</summary>
/// <param name="Status">The outcome, which fixes the exit code.</param>
/// <param name="Messages">What to tell the operator, in order, before anything else is printed.</param>
/// <param name="Facts">The report of a started run, or null when the step stopped.</param>
public sealed record RunStartOutcome(RunStartStatus Status, IReadOnlyList<RunStartMessage> Messages, RunStartFacts? Facts);

/// <summary>Fetches, resolves the base and creates the branch a run works on, before anyone investigates or plans.</summary>
public interface IRunStartService
{
    /// <summary>Runs the step in the repository the request names.</summary>
    /// <param name="request">The branch, the resume target and the configuration.</param>
    RunStartOutcome Start(RunStartRequest request);
}
