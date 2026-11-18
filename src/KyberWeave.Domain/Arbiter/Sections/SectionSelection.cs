using System.Text;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;

namespace KyberWeave.Core.Arbiter.Sections;

/// <summary>The outcome of selecting the sections of one document for a task.</summary>
/// <param name="Selected">The sections the worker should read, in document order. The title section is always included.</param>
/// <param name="FailedSafe">True when the selector was unsure, the provider did not answer, or the provider failed; <paramref name="Selected"/> is then every section.</param>
public sealed record SectionSelection(IReadOnlyList<DocumentSection> Selected, bool FailedSafe);
