using System.Text;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Review;

namespace KyberWeave.Application.Review;

/// <summary>
/// The verdict command's four decision-model steps and the lens-applicability step of <c>review seats</c>, each asked on
/// the seat files' own text: duplicates are merged and minor nitpicks dropped before the verdict is computed, then each
/// refutation candidate's claim is checked against the cited code and each finding's severity is compared with its
/// stated impact. Only the first two change what the verdict counts, and every change is reported.
/// </summary>
/// <param name="sources">Reads the lines a finding cites.</param>
/// <param name="files">Reads the files a finding's evidence cites, under a size bound.</param>
public sealed class ReviewDecisionSteps(ICitedSourceFiles sources, IContainedFileReader files) : IReviewDecisionSteps
{
    /// <summary>
    /// Asks, for each pair of findings on the same file within 20 lines, whether they name the same defect, and folds
    /// every confident pair into one finding. A rule in shadow logs its answers and merges nothing.
    /// </summary>
    public async Task<(List<ReviewFinding> Kept, List<MergedFindings> Merged)> MergeDuplicatesAsync(
        IReviewDecisionModel model, IReadOnlyList<ReviewFinding> findings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(findings);
        ArbiterRule? rule = model.Rule(ReviewVerdictRules.DuplicateRule);
        if (!model.Configured || rule is null || findings.Count < 2)
            return ([.. findings], []);

        (List<(int A, int B)> pairs, int candidateCount, int leftOut) = ReviewVerdictRules.NearbyPairs(findings);
        model.NoteSkipped(candidateCount - pairs.Count);
        model.NoteFindingsLeftOut(leftOut);
        List<ReviewQuestion> questions = [.. pairs.Select(pair => new ReviewQuestion(
            ReviewVerdictRules.PairSubject(findings[pair.A], findings[pair.B]),
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["finding.excerpt"] = ReviewDecisionText.FindingText(findings[pair.A]),
                ["candidate.text"] = ReviewDecisionText.FindingText(findings[pair.B]),
            }))];

        IReadOnlyDictionary<string, ReviewAnswer> answers = await model.AskAsync(
            rule,
            questions,
            (_, answer) => answer.Answer == "same-defect" ? rule.Shadow ? "shadow: would merge" : "merged" : "no effect",
            cancellationToken).ConfigureAwait(false);

        int[] parent = [.. Enumerable.Range(0, findings.Count)];
        Dictionary<int, double?> joinedAt = [];
        foreach ((int a, int b) in pairs)
        {
            ReviewAnswer answer = answers[ReviewVerdictRules.PairSubject(findings[a], findings[b])];
            if (rule.Shadow || answer.Answer != "same-defect")
                continue;

            parent[ReviewVerdictRules.Find(parent, b)] = ReviewVerdictRules.Find(parent, a);
            joinedAt[b] = answer.Score;
            joinedAt.TryAdd(a, answer.Score);
        }

        List<(int Index, ReviewFinding Finding)> keptAt = [];
        List<MergedFindings> merged = [];
        foreach (IGrouping<int, int> group in Enumerable.Range(0, findings.Count).GroupBy(index => ReviewVerdictRules.Find(parent, index)))
        {
            int[] members = [.. group];
            int winner = members
                .OrderByDescending(index => findings[index].Severity)
                .ThenByDescending(index => findings[index].Confidence)
                .ThenByDescending(index => (findings[index].Evidence ?? string.Empty).Length)
                .ThenBy(index => index)
                .First();
            keptAt.Add((winner, findings[winner]));
            if (members.Length > 1)
            {
                merged.Add(new MergedFindings(
                    findings[winner],
                    [.. members.Where(index => index != winner).Select(index => (findings[index], joinedAt.GetValueOrDefault(index)))]));
            }
        }

        // The verdict keeps seat order, so the kept list is put back in the order the seats gave.
        keptAt.Sort((left, right) => left.Index.CompareTo(right.Index));
        return ([.. keptAt.Select(entry => entry.Finding)], merged);
    }

    /// <summary>
    /// Asks whether each minor finding is a nitpick and drops the confident ones. Critical and major findings are never
    /// asked. A rule in shadow logs its answers and drops nothing.
    /// </summary>
    public async Task<(List<ReviewFinding> Kept, List<(ReviewFinding Finding, double? Confidence)> Nitpicks)> DropNitpicksAsync(
        IReviewDecisionModel model, IReadOnlyList<ReviewFinding> findings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(findings);
        ArbiterRule? rule = model.Rule(ReviewVerdictRules.NitpickRule);
        if (!model.Configured || rule is null)
            return ([.. findings], []);

        // A severity the host set by override is the host's grading; the model does not second-guess it.
        List<ReviewQuestion> questions = [.. findings
            .Where(finding => finding.Severity == ReviewSeverity.Minor && finding.OverrideSource is null)
            .Select(finding => new ReviewQuestion(
                finding.Id,
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["finding.excerpt"] = ReviewDecisionText.FindingText(finding) }))];
        IReadOnlyDictionary<string, ReviewAnswer> answers = await model.AskAsync(
            rule,
            questions,
            (_, answer) => answer.Answer == "nitpick" ? rule.Shadow ? "shadow: would drop" : "dropped as nitpick" : "no effect",
            cancellationToken).ConfigureAwait(false);

        List<ReviewFinding> kept = [];
        List<(ReviewFinding, double?)> nitpicks = [];
        foreach (ReviewFinding finding in findings)
        {
            if (!rule.Shadow
                && finding.Severity == ReviewSeverity.Minor
                && finding.OverrideSource is null
                && answers.TryGetValue(finding.Id, out ReviewAnswer? answer)
                && answer.Answer == "nitpick")
            {
                nitpicks.Add((finding, answer.Score));
            }
            else
            {
                kept.Add(finding);
            }
        }

        return (kept, nitpicks);
    }

    /// <summary>
    /// Checks each refutation candidate before a refuter is sent. The excerpt must be in the cited lines of the file as
    /// it is now; then the model is asked whether those lines support the claim. Only a confident "supports" spares the
    /// refuter: a confident "contradicts" still goes to one, because a major finding is never lost on this answer alone.
    /// </summary>
    public async Task<IReadOnlyList<ClaimCheck>> CheckClaimsAsync(
        IReviewDecisionModel model, string repositoryRoot, IReadOnlyList<ReviewFinding> candidates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(candidates);
        ArbiterRule? rule = model.Rule(ReviewVerdictRules.ClaimRule);
        if (!model.Configured || rule is null)
            return [.. candidates.Select(finding => new ClaimCheck(finding, true, null))];

        Dictionary<string, string> quoteProblems = new(StringComparer.Ordinal);
        List<ReviewQuestion> questions = [];

        // Each candidate costs file reads, so the work is bounded where it is done, not after the model has dropped the
        // questions past the cap: the loop stops once a step's questions are full or ReviewVerdictRules.MaxFindingsPerStep candidates
        // were looked at, and the rest are counted and left to the refuter as if unasked.
        int looked = 0;
        foreach (ReviewFinding finding in candidates)
        {
            if (questions.Count >= ReviewVerdictRules.MaxQuestionsPerStep)
            {
                model.NoteSkipped(candidates.Count - looked);
                break;
            }

            if (looked >= ReviewVerdictRules.MaxFindingsPerStep)
            {
                model.NoteFindingsLeftOut(candidates.Count - looked);
                break;
            }

            looked++;
            string? readFailure = null;
            string? surroundings = Surroundings(repositoryRoot, finding, type => readFailure = type);
            string? problem = surroundings is null ? $"the cited file could not be read{(readFailure is null ? string.Empty : $" ({readFailure})")}"
                : string.IsNullOrWhiteSpace(finding.Excerpt) ? "the finding quotes no code"
                : !ReviewSeatInput.ExcerptWithinLimits(finding.Excerpt)
                    ? $"its excerpt is over {ReviewSeatInput.MaxExcerptCharacters} characters or {ReviewSeatInput.MaxExcerptLines} lines, so the quote was not verified"
                : string.IsNullOrWhiteSpace(finding.Claim) ? "the finding states no claim"
                : !ReviewVerdictRules.ExcerptIsIn(finding.Excerpt, surroundings) ? "its excerpt is not at the cited lines"
                : null;
            if (problem is not null)
            {
                quoteProblems[finding.Id] = problem;
                continue;
            }

            questions.Add(new ReviewQuestion(
                finding.Id,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["finding.claim"] = ReviewSeatInput.Clip(finding.Claim),
                    ["finding.excerpt"] = finding.Excerpt,
                    ["file.surroundings"] = surroundings + EvidenceLines(repositoryRoot, finding),
                }));
        }

        IReadOnlyDictionary<string, ReviewAnswer> answers = await model.AskAsync(
            rule,
            questions,
            (_, answer) => answer.Answer switch
            {
                "supports" => "claim confirmed, no refuter",
                "contradicts" => "claim contradicted, refuter still sent",
                _ => "refuter sent",
            },
            cancellationToken).ConfigureAwait(false);

        List<ClaimCheck> checks = [];
        foreach (ReviewFinding finding in candidates)
        {
            if (quoteProblems.TryGetValue(finding.Id, out string? problem))
            {
                checks.Add(new ClaimCheck(finding, true, $"not asked: {problem}"));
                continue;
            }

            ReviewAnswer answer = answers.GetValueOrDefault(finding.Id) ?? ReviewAnswer.Undecidable;
            checks.Add(answer.Answer switch
            {
                "supports" => new ClaimCheck(finding, false, $"the cited code supports the claim (confidence {ReviewDecisionText.Format(answer.Score)})"),
                "contradicts" => new ClaimCheck(finding, true, $"the cited code contradicts the claim (confidence {ReviewDecisionText.Format(answer.Score)}); the refuter decides"),
                _ => new ClaimCheck(finding, true, null),
            });
        }

        return checks;
    }

    /// <summary>
    /// Asks whether each finding's stated severity matches its stated impact. Advisory only: the verdict keeps the
    /// severity the seat gave, and the report notes a confident mismatch for the lead reviewer.
    /// </summary>
    public async Task<IReadOnlyList<(ReviewFinding Finding, string Direction, double? Confidence)>> CheckSeveritiesAsync(
        IReviewDecisionModel model, IReadOnlyList<ReviewFinding> findings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(findings);
        ArbiterRule? rule = model.Rule(ReviewVerdictRules.SeverityRule);
        if (!model.Configured || rule is null)
            return [];

        List<ReviewQuestion> questions = [.. findings.Where(finding => finding.OverrideSource is null).Select(finding => new ReviewQuestion(
            finding.Id,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["finding.severity"] = ReviewVerdictRules.SeverityName(finding.Severity),
                ["finding.claim"] = ReviewSeatInput.Clip(finding.Claim),
                ["finding.failure-scenario"] = ReviewSeatInput.Clip(finding.FailureScenario),
            }))];
        IReadOnlyDictionary<string, ReviewAnswer> answers = await model.AskAsync(
            rule,
            questions,
            (_, answer) => answer.Answer is "too-high" or "too-low" ? $"severity note ({answer.Answer})" : "no effect",
            cancellationToken).ConfigureAwait(false);

        return [.. findings
            .Where(finding => finding.OverrideSource is null)
            .Select(finding => (finding, answer: answers.GetValueOrDefault(finding.Id) ?? ReviewAnswer.Undecidable))
            .Where(pair => pair.answer.Answer is "too-high" or "too-low")
            .Select(pair => (pair.finding, pair.answer.Answer == "too-high" ? "high" : "low", pair.answer.Score))];
    }

    /// <summary>
    /// Asks, for each finding that cites no host severity override, whether it belongs to the class each override row
    /// relevant to its lens and file describes, and grades a confident match at the row's severity. When several rows
    /// match one finding, the most confident wins, then the row first in the table. A rule in shadow logs its answers
    /// and changes nothing. With no model, no rule or no rows nothing is asked.
    /// </summary>
    /// <returns>The findings in the order given, each matched one graded at its row; and each match with its confidence.</returns>
    public async Task<(List<ReviewFinding> Findings, List<(ReviewFinding Finding, SeverityOverrideRow Row, double? Confidence)> Matched)> MatchOverridesAsync(
        IReviewDecisionModel model, SeverityOverrides severityOverrides, IReadOnlyList<ReviewFinding> findings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(severityOverrides);
        ArgumentNullException.ThrowIfNull(findings);
        ArbiterRule? rule = model.Rule(ReviewVerdictRules.OverrideRule);
        if (!model.Configured || rule is null || severityOverrides.Rows.Count == 0)
            return ([.. findings], []);

        // The findings with the most at stake are asked first; the sort is stable, so seat order breaks ties. Candidates
        // are built in that order; once the step's questions are full the rows of later findings are only counted, and
        // at most ReviewVerdictRules.MaxFindingsPerStep findings are looked at, so the work is bounded by that and the host's row count.
        int[] order = [.. Enumerable.Range(0, findings.Count)
            .Where(index => findings[index].Override is null && findings[index].OverrideSource is null)
            .OrderByDescending(index => findings[index].Severity)];
        List<(int Index, SeverityOverrideRow Row, string Subject)> pairs = [];
        int looked = 0;
        foreach (int index in order)
        {
            if (looked >= ReviewVerdictRules.MaxFindingsPerStep)
                break;

            looked++;
            ReviewFinding finding = findings[index];
            foreach (SeverityOverrideRow row in severityOverrides.RowsForFinding(finding.Lens, finding.File))
            {
                if (pairs.Count >= ReviewVerdictRules.MaxQuestionsPerStep)
                {
                    model.NoteSkipped(1);
                    continue;
                }

                pairs.Add((index, row, $"{finding.Id} ~ {row.Id}"));
            }
        }

        model.NoteFindingsLeftOut(order.Length - looked);
        List<ReviewQuestion> questions = [.. pairs.Select(pair => new ReviewQuestion(
            pair.Subject,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["finding.claim"] = ReviewDecisionText.FindingText(findings[pair.Index]),
                ["override.class"] = pair.Row.FindingClass,
            }))];

        IReadOnlyDictionary<string, ReviewAnswer> answers = await model.AskAsync(
            rule,
            questions,
            (question, answer) => answer.Answer == "belongs"
                ? rule.Shadow ? "shadow: would set the severity by override" : "severity set by override"
                : "no effect",
            cancellationToken).ConfigureAwait(false);

        List<ReviewFinding> result = [.. findings];
        List<(ReviewFinding, SeverityOverrideRow, double?)> matched = [];
        if (rule.Shadow)
            return (result, matched);

        foreach (IGrouping<int, (int Index, SeverityOverrideRow Row, string Subject)> group in pairs.GroupBy(pair => pair.Index))
        {
            (SeverityOverrideRow Row, double? Score)? best = null;
            foreach ((_, SeverityOverrideRow row, string subject) in group)
            {
                ReviewAnswer answer = answers.GetValueOrDefault(subject) ?? ReviewAnswer.Undecidable;
                if (answer.Answer != "belongs")
                    continue;
                if (best is null || (answer.Score ?? 0) > (best.Value.Score ?? 0))
                    best = (row, answer.Score);
            }

            if (best is { } winner)
            {
                ReviewFinding graded = SeverityOverrideRules.Apply(findings[group.Key], winner.Row, SeverityOverrides.ModelSource);
                result[group.Key] = graded;
                matched.Add((graded, winner.Row, winner.Score));
            }
        }

        return (result, matched);
    }

    /// <summary>
    /// Asks the question for each seated judgement lens and returns the lenses the model confidently ruled out, each
    /// with the reason <c>--not-seated</c> records.
    /// </summary>
    /// <param name="model">The decision model; one not configured asks nothing.</param>
    /// <param name="seated">The lenses the file-pattern step seated.</param>
    /// <param name="changeSummary">The compact change summary from <see cref="ReviewLensRules.Summarize"/>.</param>
    /// <param name="cancellationToken">Cancels the provider calls.</param>
    public async Task<IReadOnlyDictionary<string, string>> NotApplicableLensesAsync(
        IReviewDecisionModel model, IReadOnlyList<string> seated, string changeSummary, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(seated);
        Dictionary<string, string> notSeated = new(StringComparer.Ordinal);
        ArbiterRule? rule = model.Rule(ReviewLensRules.Rule);
        if (!model.Configured || rule is null)
            return notSeated;

        IReadOnlyDictionary<string, ArbiterSquadLens> lenses = ArbiterSquadCatalog.LoadEmbedded().Lenses;
        List<ReviewQuestion> questions = [.. seated
            .Where(lens => !ReviewLensRules.NeverAsked.Contains(lens, StringComparer.Ordinal) && lenses.ContainsKey(lens))
            .Select(lens => new ReviewQuestion(
                lens,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["lens.applicability"] = lenses[lens].Applicability,
                    ["review.diff-summary"] = changeSummary,
                },
                lens))];
        IReadOnlyDictionary<string, ReviewAnswer> answers = await model.AskAsync(
            rule,
            questions,
            (_, answer) => answer.Answer == "not-applicable" ? "not seated" : "seated",
            cancellationToken).ConfigureAwait(false);

        foreach ((string lens, ReviewAnswer answer) in answers)
        {
            if (answer.Answer == "not-applicable")
            {
                // A noul's score is the probability the lens applies, so the confidence of "does not apply" is its complement.
                double? confidence = answer.Score is double applies ? 1 - applies : null;
                notSeated[lens] = $"decision model: not applicable (confidence {ReviewDecisionText.Format(confidence)})";
            }
        }

        return notSeated;
    }

    /// <inheritdoc />
    public string? Surroundings(string repositoryRoot, ReviewFinding finding, Action<string>? onFailure = null)
    {
        ArgumentNullException.ThrowIfNull(finding);
        try
        {
            string? path = Resolve(repositoryRoot, finding.File, onFailure);
            if (path is null)
                return null;

            (long first, long count) = ReviewVerdictRules.SurroundingsRange(finding);
            return ReviewVerdictRules.SurroundingsText(sources.ReadWindow(path, first, count));
        }
        catch (Exception ex) when (ReviewSeatInput.IsFileFailure(ex))
        {
            onFailure?.Invoke(ex.GetType().Name);
            return null;
        }
    }

    /// <inheritdoc />
    public string EvidenceLines(string repositoryRoot, ReviewFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        if (string.IsNullOrWhiteSpace(finding.Evidence))
            return string.Empty;

        string evidence = finding.Evidence;
        // The evidence is seat-written: only its first characters are scanned, and only the first few citations are read.
        StringBuilder text = new();
        int added = 0;
        int notes = 0;
        foreach (System.Text.RegularExpressions.Match match in ReviewVerdictRules.EvidenceCitation().Matches(ReviewSeatInput.Clip(evidence, ReviewSeatInput.MaxTextCharacters)).Take(ReviewVerdictRules.MaxEvidenceCitations))
        {
            string file = match.Groups["file"].Value;
            string? failure = null;
            string? path = Resolve(repositoryRoot, file, type => failure = type);
            if (path is null && failure is null)
                continue;

            string[] lines = [];
            if (path is not null)
            {
                try
                {
                    lines = files.ReadLines(path, ReviewSeatInput.MaxFileBytes);
                }
                catch (Exception ex) when (ReviewSeatInput.IsFileFailure(ex) || ex is InvalidDataException)
                {
                    failure = ex.GetType().Name;
                }
            }

            if (failure is not null)
            {
                // Said in the question, so the model does not read a missing citation as one that has nothing to show.
                text.Append('\n').Append(file).Append(": could not be read (").Append(failure).Append(')');
                notes++;
                continue;
            }

            foreach (int number in ReviewVerdictRules.CitedNumbers(match))
            {
                if (number < 1 || number > lines.Length || added >= ReviewVerdictRules.MaxEvidenceLines)
                    continue;

                text.Append('\n').Append(file).Append(':').Append(number).Append(": ").Append(ReviewSeatInput.Clip(lines[number - 1].Trim()));
                added++;
            }
        }

        return added + notes == 0 ? string.Empty : "\n\nLines the finding's evidence cites, as they are in the tree now:" + text;
    }

    /// <summary>
    /// The full path of a file inside the repository, as long as it exists, is small enough to read and is reached
    /// without a symbolic link; null for a name that is malformed, over-long, outside the repository, absent or a link.
    /// The name is seat-written, so every way it can fail to resolve is an answer, not an error.
    /// </summary>
    private string? Resolve(string repositoryRoot, string file, Action<string>? onFailure)
    {
        try
        {
            return sources.Resolve(repositoryRoot, file, ReviewSeatInput.MaxFileBytes);
        }
        catch (Exception ex) when (ReviewSeatInput.IsFileFailure(ex))
        {
            onFailure?.Invoke(ex.GetType().Name);
            return null;
        }
    }
}
