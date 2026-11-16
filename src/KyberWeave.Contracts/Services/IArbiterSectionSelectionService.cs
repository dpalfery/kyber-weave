using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Sections;

namespace KyberWeave.Contracts.Services;

/// <summary>Reads one repository document and selects the sections of it a task needs.</summary>
public interface IArbiterSectionSelectionService
{
    /// <summary>
    /// Selects the sections of the document at <paramref name="documentPath"/> that the task needs. A document that
    /// cannot be read, or that is over the size cap, selects nothing and reports itself as failed safe.
    /// </summary>
    /// <param name="documentPath">The document's absolute path; it must exist.</param>
    /// <param name="task">The task the worker is about to do, in plain words.</param>
    /// <param name="provider">The provider that answers the questions.</param>
    /// <param name="ct">Cancels the read and the provider call.</param>
    /// <returns>The selection; never throws for a read or provider failure.</returns>
    Task<SectionSelection> SelectAsync(
        string documentPath,
        string task,
        IArbiterProvider provider,
        CancellationToken ct = default);
}
