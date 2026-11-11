using System.Text;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;

namespace KyberWeave.Core.Arbiter.Sections;

/// <summary>The outcome of selecting the sections of one document for a task.</summary>
/// <param name="Selected">The sections the worker should read, in document order. The title section is always included.</param>
/// <param name="FailedSafe">True when the selector was unsure, the provider did not answer, or the provider failed; <paramref name="Selected"/> is then every section.</param>
public sealed record SectionSelection(IReadOnlyList<DocumentSection> Selected, bool FailedSafe);

/// <summary>
/// Asks the provider, through <c>KW-ARB-TRIM-001</c>, which sections of a document a task needs. One question is
/// batched per candidate section, up to <see cref="StateBudget.MaxQuestions"/>; the first section is the title and is always kept, and candidates past the cap are kept unasked. The selector fails safe: any
/// answer that is missing, below the rule's floor, or not sent returns every section.
/// </summary>
/// <param name="provider">The provider that answers the questions; <c>NoneProvider</c> answers nothing and so keeps every section.</param>
public sealed class SectionSelector(IArbiterProvider provider)
{
    /// <summary>The shipped rule that asks whether a section is relevant.</summary>
    public const string TrimRuleId = "KW-ARB-TRIM-001";

    /// <summary>The largest document the selector reads, in bytes. A larger one fails safe rather than being read whole.</summary>
    public const long MaxDocumentBytes = 1024 * 1024;

    private const string Relevant = "relevant";
    private const string NotRelevant = "not-relevant";
    private const string TaskFact = "task.text";
    private const string CandidatesFact = "section.candidates";

    /// <summary>
    /// Reads the file as text, or returns null when it holds more than <paramref name="limitBytes"/>. The bound applies to
    /// the bytes actually read, not to a length looked up beforehand, so a file that grows after a check is still refused.
    /// The byte-order mark is detected as <c>File.ReadAllTextAsync</c> detects it.
    /// </summary>
    internal static async Task<string?> ReadBoundedAsync(string path, long limitBytes, CancellationToken ct)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true);
        using MemoryStream bytes = new();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            if (bytes.Length + read > limitBytes)
            {
                return null;
            }

            await bytes.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        using StreamReader reader = new(
            new MemoryStream(bytes.GetBuffer(), 0, (int)bytes.Length), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Selects the sections of the document at <paramref name="documentPath"/> that the task needs.</summary>
    /// <param name="documentPath">The path of the markdown document; it must exist.</param>
    /// <param name="task">The task the worker is about to do, in plain words.</param>
    /// <param name="ct">Cancels the provider call.</param>
    /// <returns>The selection; never throws for a provider failure.</returns>
    public async Task<SectionSelection> SelectAsync(string documentPath, string task, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentPath);
        ArgumentNullException.ThrowIfNull(task);

        // A document that cannot be read or is over the size cap yields no sections to choose from. The selection then
        // fails safe with nothing selected, and the caller falls back to the whole document it already holds.
        string? text;
        try
        {
            text = await ReadBoundedAsync(documentPath, MaxDocumentBytes, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SectionSelection([], FailedSafe: true);
        }

        if (text is null)
        {
            return new SectionSelection([], FailedSafe: true);
        }

        IReadOnlyList<DocumentSection> sections = DocumentSectionIndex.Split(text);
        if (sections.Count <= 1)
        {
            return new SectionSelection(sections, FailedSafe: false);
        }

        ArbiterRule? shipped = ArbiterConfig.ProductDefaults.Rules
            .FirstOrDefault(rule => string.Equals(rule.Id, TrimRuleId, StringComparison.Ordinal) && rule.Ask is not null);
        if (shipped?.Ask is not { } ask)
        {
            return FailSafe(sections);
        }

        // Only the first MaxQuestions candidates are asked, to stay inside the step-1 batch limit. The rest are kept
        // unasked: unsure keeps, so a section the provider never saw is never dropped.
        int asked = Math.Min(sections.Count - 1, StateBudget.MaxQuestions);

        // Each candidate becomes its own question keyed by rule id plus section id, so one batched call answers all.
        Dictionary<string, ArbiterFact> facts = new(StringComparer.Ordinal)
        {
            [TaskFact] = new ArbiterFact(task, ArbiterFactLabel.Asserted),
            [CandidatesFact] = new ArbiterFact(sections.Skip(1).Take(asked).Select(section => section.Id).ToList(), ArbiterFactLabel.Derived),
        };
        List<ArbiterRule> questions = [];
        Dictionary<string, DocumentSection> byKey = new(StringComparer.Ordinal);
        for (int index = 1; index <= asked; index++)
        {
            DocumentSection section = sections[index];
            string key = TrimRuleId + "@" + section.Id;
            string textFact = $"candidate[{TrimRuleId}][{index}].text";
            facts[textFact] = new ArbiterFact(section.Text, ArbiterFactLabel.Derived);
            RuleAsk expanded = ask with
            {
                ForEach = null,
                State = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["task"] = TaskFact,
                    ["section"] = textFact,
                },
            };
            questions.Add(shipped with { Id = key, Ask = expanded });
            byKey[key] = section;
        }

        ArbiterStep1BatchResult batch;
        try
        {
            batch = await provider.AskStep1Async(questions, new ArbiterFactSet(facts), null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ProviderFailures.IsAbsorbable(ex))
        {
            // The provider answered nothing usable, so every section stays; the selection says it failed safe.
            return FailSafe(sections);
        }

        if (!batch.RequestSent)
        {
            return FailSafe(sections);
        }

        double floor = ask.ConfidenceAtLeast ?? 1.0;
        HashSet<string> keep = new(StringComparer.Ordinal) { sections[0].Id };
        foreach (KeyValuePair<string, DocumentSection> pair in byKey)
        {
            if (!batch.AnswersByRule.TryGetValue(pair.Key, out string? answer)
                || batch.ScoresByRule is null
                || !batch.ScoresByRule.TryGetValue(pair.Key, out ArbiterStep1Score? score)
                || score.Confidence is not double confidence
                || confidence < floor)
            {
                return FailSafe(sections);
            }

            if (string.Equals(answer, Relevant, StringComparison.Ordinal))
            {
                keep.Add(pair.Value.Id);
            }
            else if (!string.Equals(answer, NotRelevant, StringComparison.Ordinal))
            {
                return FailSafe(sections);
            }
        }

        for (int index = asked + 1; index < sections.Count; index++)
        {
            keep.Add(sections[index].Id);
        }

        List<DocumentSection> selected = [.. sections.Where(section => keep.Contains(section.Id))];
        return new SectionSelection(selected, FailedSafe: false);
    }

    private static SectionSelection FailSafe(IReadOnlyList<DocumentSection> sections) =>
        new(sections, FailedSafe: true);
}
