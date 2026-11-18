namespace KyberWeave.Core.RunStart;

/// <summary>One task a branch records as finished, and the report the commit that finished it carried.</summary>
/// <param name="Id">The task id the commit's <c>Task:</c> trailer names.</param>
/// <param name="Report">
///     The commit body above the trailer area, with its paragraphs kept and its blank edge lines trimmed;
///     the empty string when the commit recorded the task with no report.
/// </param>
public sealed record RunStartCompletedTask(string Id, string Report);
