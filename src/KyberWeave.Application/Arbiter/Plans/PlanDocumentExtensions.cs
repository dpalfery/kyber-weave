using KyberWeave.Core.Arbiter.Facts;

namespace KyberWeave.Core.Arbiter.Plans;

/// <summary>
/// The facts a parsed plan projects. They live here rather than on <see cref="PlanDocument"/> so the record stays data.
/// </summary>
public static class PlanDocumentExtensions
{
    /// <summary>
    /// The <c>plan.test-contract.row</c> fact for <paramref name="taskId"/>: the row's
    /// <c>RED evidence required</c> cell, with every way of saying no test is owed read as
    /// <c>Not applicable</c>. Null when the task has no row or the cell is empty.
    /// </summary>
    /// <param name="document">The parsed plan.</param>
    /// <param name="taskId">The task whose contract row is read.</param>
    /// <returns>The row's red-evidence requirement, or null when the task has none.</returns>
    public static string? RedEvidenceRequirement(this PlanDocument document, string taskId)
    {
        ArgumentNullException.ThrowIfNull(document);

        string? cell = document.ContractRows
            .FirstOrDefault(row => string.Equals(row.TaskId, taskId, StringComparison.Ordinal))
            ?.RedEvidenceRequired?.Trim();
        if (string.IsNullOrEmpty(cell))
        {
            return null;
        }

        return NoRedEvidence.IsStatedBy(cell) ? "Not applicable" : cell;
    }
}
