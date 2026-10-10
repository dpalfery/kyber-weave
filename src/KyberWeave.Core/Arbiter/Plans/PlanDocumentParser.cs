using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Parsing;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using YamlDotNet.Core;

namespace KyberWeave.Core.Arbiter.Plans;

/// <summary>
/// Parses a plan or a spec task artifact as authored, with no generated artifacts
/// (Req 9.1): the parse yields the document's mode, tasks, files, dependencies,
/// contract rows and out-of-scope paths.
/// </summary>
/// <remarks>
/// <para>
/// The parser is a pure function of the file text and writes nothing. Markdig — with
/// CommonMark, pipe tables and YAML front matter — supplies the structure (frontmatter,
/// headings, tables); regex supplies the labels (Req 9.2). Findings surface as
/// diagnostics rather than exceptions: an archived plan whose dependency names a task
/// that has since been renumbered still parses, and the arbiter decides what the stale
/// reference means for readiness (READY-001 answers <c>unknown-dependency</c>).
/// </para>
/// <para>
/// The labels Req 9.3 names are matched as families on their leading word, so the older
/// variants in <c>docs/archive/plans/</c> also parse: the Files family covers <c>Files /
/// symbols</c>, <c>Files/symbols owned</c> and <c>Files owned</c>; the Depends family
/// covers <c>Depends on</c>, <c>Depends-on</c> and the archived <c>Dependencies</c>;
/// the Skills family covers <c>Required skills</c> and <c>Required skill</c>. Bold is
/// optional, the colon may sit inside or outside the bold, and a leading <c>- </c> is
/// optional — every one of those shapes occurs in the archive.
/// </para>
/// </remarks>
public static partial class PlanDocumentParser
{
    /// <summary>A dependency token names no task in the same document (READY-001 answers unknown-dependency).</summary>
    public const string UnknownDependencyRule = "KW-ARB-PARSE-001";

    /// <summary>A task lists no files; its file-scope checks are skipped and the skip is logged (D30).</summary>
    public const string MissingFilesRule = "KW-ARB-PARSE-002";

    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseYamlFrontMatter().UsePipeTables().Build();

    private static readonly char[] SkillSeparators = ['/', ','];

    // The heading grammar from Req 9.3: `### T1: RED, Claude renderer contract` and
    // `### T1 — Establish the failing regression contracts` both occur in the archive.
    [GeneratedRegex(
        @"^T(?<n>\d+)(?<sub>[a-z])?(?<suffix>(?:-[A-Za-z0-9]+)*)(?:\s*:\s*|\s+[—–-]\s+)?(?<title>.*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex PlanTaskHeadingRegex();

    // The spec checkbox grammar from D30: `- [ ] 2.1 Title`.
    [GeneratedRegex(
        @"^- \[(?<tick>[ xX])\] (?<id>\d+(?:\.\d+)?)\.?[ \t]+(?<title>.+)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SpecTaskItemRegex();

    [GeneratedRegex(@"\bT\d+[a-z]?(?:-[A-Za-z0-9]+)*", RegexOptions.CultureInvariant)]
    private static partial Regex PlanDependencyTokenRegex();

    // Spec-grammar dependency tokens are dotted numbers and only count directly after
    // the label, so a prose "item 3" never becomes a dependency.
    [GeneratedRegex(@"(?<![\w-])(?<id>\d+(?:\.\d+)?)(?![\w-])", RegexOptions.CultureInvariant)]
    private static partial Regex SpecDependencyTokenRegex();

    [GeneratedRegex(@"^T\d+[a-z]?(?:-[A-Za-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex PlanTaskIdRegex();

    [GeneratedRegex(@"^\d+(?:\.\d+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SpecTaskIdRegex();

    [GeneratedRegex(
        @"\b(?:files\b(?:(?:\s*/\s*|\s)symbols)?(?:\s+owned)?|scope)[ \t]*:",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FilesLabelRegex();

    [GeneratedRegex(@"\bdepend(?:encies|s[ \t]*-?[ \t]*on)\b[ \t]*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DependsLabelRegex();

    [GeneratedRegex(@"\b(?:required[ \t]+skills?|skills)\b[ \t]*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SkillsLabelRegex();

    [GeneratedRegex(@"`([^`\n]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex BacktickSpanRegex();

    [GeneratedRegex(@"^[ \t]*-[ \t]+", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex BulletLineRegex();

    [GeneratedRegex(@"<[^<>\n]*>", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderSegmentRegex();

    // A file extension starts with a letter: `README.md` and `.exe` are paths, version
    // numbers like 0.18.5 are not.
    [GeneratedRegex(@"\.[A-Za-z][A-Za-z0-9]{0,7}$", RegexOptions.CultureInvariant)]
    private static partial Regex FileExtensionRegex();

    // Skill names in this repository are kebab-case; `(D9)` after a heading is a
    // decision id, not an agent.
    [GeneratedRegex(@"^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SkillNameRegex();

    [GeneratedRegex(@"\((?<inner>[^()]*)\)$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingSkillSuffixRegex();

    [GeneratedRegex(@"^Development mode:[ \t]*(?<mode>.+)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex DevelopmentModeBodyRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    /// <summary>Parses plan or spec task artifact text into a <see cref="PlanDocument"/>.</summary>
    /// <param name="markdown">The file text as authored. The parser writes nothing.</param>
    /// <summary>
    /// The SHA-256 of a plan's content, lowercase hex. The identity a decision log entry
    /// carries so a later decision can tell an unchanged plan from an amended one.
    /// </summary>
    /// <remarks>
    /// Computed here, next to the parse, so the digest a decision records is the digest
    /// of the exact text it parsed. Re-deriving it from the parsed document would hash
    /// something the file never contained, and two hosts would then disagree on whether
    /// a plan had been amended.
    /// </remarks>
    public static string Digest(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(markdown)));
    }

    public static PlanDocument Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        string text = markdown.Replace("\r\n", "\n", StringComparison.Ordinal);
        MarkdownDocument document = Markdown.Parse(text, Pipeline);
        (string? status, string? frontmatterMode, string body) = ReadFrontmatter(text);

        List<HeadingLine> headings = [.. document.Descendants<HeadingBlock>()
            .OrderBy(heading => heading.Span.Start)
            .Select(heading => new HeadingLine(
                heading.Level,
                FlattenInlines(heading.Inline).Trim(),
                heading.Span.Start,
                BodyStart(text, heading.Span.End)))];

        HeadingLine? tasksHeading = headings.FirstOrDefault(heading => heading.Level == 2 && IsTasksHeading(heading.Text));
        bool hasTasks = tasksHeading is not null;
        int tasksStart = tasksHeading?.BodyStart ?? text.Length;
        int tasksEnd = tasksHeading is null ? text.Length : SectionEnd(headings, tasksHeading, text.Length);

        List<TaskBuild> builds = [];
        if (hasTasks)
        {
            CollectPlanTasks(text, headings, tasksStart, tasksEnd, builds);
            CollectSpecTasks(text[tasksStart..tasksEnd], tasksStart, builds);
        }

        builds.Sort((left, right) => left.Position.CompareTo(right.Position));
        HashSet<string> ids = [.. builds.Select(build => build.Id)];
        List<PlanTask> tasks = [];
        List<Diagnostic> diagnostics = [];
        foreach (TaskBuild build in builds)
        {
            List<string> dependsOn = [];
            foreach (string token in build.DependsTokens)
            {
                if (ids.Contains(token))
                {
                    if (!dependsOn.Contains(token))
                    {
                        dependsOn.Add(token);
                    }
                }
                else
                {
                    diagnostics.Add(new Diagnostic(
                        UnknownDependencyRule,
                        Severity.Warning,
                        $"Dependency token '{token}' on task '{build.Id}' names no task in this document.",
                        build.Id,
                        Hint: "Dependency tokens must name a task id of this document ('T4a', 'T1-RED-...' or, in the spec grammar, '2.1'). Fix the id or remove the dependency."));
                }
            }

            if (build.Files.Count == 0)
            {
                diagnostics.Add(new Diagnostic(
                    MissingFilesRule,
                    Severity.Warning,
                    $"Task '{build.Id}' lists no files.",
                    build.Id,
                    Hint: "Declare the task's file scope under a Files/scope label, or as backticked paths in the task body; its file-scope checks are skipped."));
            }

            tasks.Add(new PlanTask(build.Id, build.Title, build.Text, build.Files, dependsOn, build.Skills, build.Checked));
        }

        List<string> outOfScope = [];
        foreach (HeadingLine heading in headings.Where(heading => heading.Level == 2 && IsOutOfScopeHeading(heading.Text)))
        {
            int end = SectionEnd(headings, heading, text.Length);
            foreach (string path in ArbiterPathRule.ExtractPaths(text[heading.BodyStart..end]))
            {
                if (!outOfScope.Contains(path))
                {
                    outOfScope.Add(path);
                }
            }
        }

        IReadOnlyList<PlanContractRow> contractRows = ReadContractRows(text, document, headings);
        string developmentMode = frontmatterMode ?? BodyDevelopmentMode(body) ?? "test-first";
        return new PlanDocument(status, developmentMode, hasTasks, tasks, outOfScope, contractRows, diagnostics);
    }

    private static (string? Status, string? Mode, string Body) ReadFrontmatter(string text)
    {
        FrontmatterReadResult frontmatter = MarkdownFrontmatterReader.Read(text);
        if (!frontmatter.HasFrontmatter)
        {
            return (null, null, text);
        }

        string? status = null;
        string? mode = null;
        try
        {
            Dictionary<string, object?>? map =
                MarkdownFrontmatterReader.Deserializer.Deserialize<Dictionary<string, object?>>(frontmatter.Yaml);
            if (map is not null)
            {
                status = map.TryGetValue("status", out object? statusValue) ? statusValue as string : null;
                mode = map.TryGetValue("development-mode", out object? modeValue) ? modeValue as string : null;
            }
        }
        catch (YamlException)
        {
            // The archive writes frontmatter that is not always valid YAML — an unquoted
            // archive-outcome value may contain a colon. Fall back to a line scan for
            // the two keys the contract reads before giving up on the block.
            foreach (string line in frontmatter.Yaml.Split('\n'))
            {
                string trimmed = line.Trim();
                if (status is null && trimmed.StartsWith("status:", StringComparison.Ordinal))
                {
                    status = ScalarValue(trimmed[("status:".Length)..]);
                }
                else if (mode is null && trimmed.StartsWith("development-mode:", StringComparison.Ordinal))
                {
                    mode = ScalarValue(trimmed[("development-mode:".Length)..]);
                }
            }
        }

        return (status, mode, frontmatter.Body);
    }

    private static string? ScalarValue(string value)
    {
        string scalar = value.Trim().Trim('"', '\'').Trim();
        return scalar.Length > 0 ? scalar : null;
    }

    private static string? BodyDevelopmentMode(string body)
    {
        Match match = DevelopmentModeBodyRegex().Match(StripEmphasis(body).Plain);
        return match.Success ? match.Groups["mode"].Value.Trim().Trim('`').Trim() : null;
    }

    private static void CollectPlanTasks(string text, List<HeadingLine> headings, int tasksStart, int tasksEnd, List<TaskBuild> builds)
    {
        List<HeadingLine> taskHeadings = [.. headings
            .Where(heading => heading.Level == 3 && heading.Start >= tasksStart && heading.Start < tasksEnd)];

        foreach (HeadingLine heading in taskHeadings)
        {
            Match match = PlanTaskHeadingRegex().Match(heading.Text);
            if (!match.Success)
            {
                continue;
            }

            string id = $"T{match.Groups["n"].Value}{match.Groups["sub"].Value}{match.Groups["suffix"].Value}";
            string title = match.Groups["title"].Value.Trim();
            int end = Math.Max(Math.Min(SectionEnd(headings, heading, tasksEnd), tasksEnd), heading.BodyStart);
            string sectionText = text[heading.BodyStart..end].Trim();
            (List<string> files, List<string> dependsTokens, List<string> labelSkills) = ParseTaskBody(sectionText, isSpec: false);

            TaskBuild build = new(id, title, sectionText, false, heading.Start)
            {
                Files = files,
                DependsTokens = dependsTokens,
            };
            build.Skills.EnsureCapacity(build.Skills.Count + labelSkills.Count);
            foreach (string skill in HeadingSkills(heading.Text).Concat(labelSkills))
            {
                if (!build.Skills.Contains(skill))
                {
                    build.Skills.Add(skill);
                }
            }

            builds.Add(build);
        }
    }

    private static void CollectSpecTasks(string sectionText, int sectionStart, List<TaskBuild> builds)
    {
        string[] lines = sectionText.Split('\n');
        int offset = 0;
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            Match match = SpecTaskItemRegex().Match(line);
            if (match.Success)
            {
                List<string> bodyLines = [];
                for (int body = index + 1; body < lines.Length && !SpecTaskItemRegex().IsMatch(lines[body]); body++)
                {
                    bodyLines.Add(lines[body]);
                }

                string bodyText = string.Join("\n", bodyLines).Trim();
                (List<string> files, List<string> dependsTokens, List<string> skills) = ParseTaskBody(bodyText, isSpec: true);
                builds.Add(new TaskBuild(
                    match.Groups["id"].Value,
                    match.Groups["title"].Value.Trim(),
                    bodyText,
                    char.IsLetter(match.Groups["tick"].ValueSpan[0]),
                    sectionStart + offset)
                {
                    Files = files,
                    DependsTokens = dependsTokens,
                    Skills = skills,
                });
            }

            offset += line.Length + 1;
        }
    }

    /// <summary>
    /// Harvests one task body's labels. Files come from the first Files-family label;
    /// the spec grammar falls back to the body's backticked paths. Dependencies come
    /// from the first Depends-family label, tokens per grammar. Skills come from every
    /// Skills-family label; the plan heading's <c>(agent)</c> suffix is added by the caller.
    /// </summary>
    private static (List<string> Files, List<string> DependsTokens, List<string> Skills) ParseTaskBody(string sectionText, bool isSpec)
    {
        (string plain, List<int> boldStarts) = StripEmphasis(sectionText);
        List<LabelMatch> labels = CollectLabels(plain);

        List<string> files = [];
        LabelMatch? filesLabel = labels.FirstOrDefault(label => label.Family == LabelFamily.Files);
        if (filesLabel is not null)
        {
            int nextLabelStart = NextLabelStart(labels, filesLabel.Start);
            int paragraphEnd = RegionEnd(plain, filesLabel.ValueStart, Math.Min(
                Math.Min(nextLabelStart, DoubleNewlineIndex(plain, filesLabel.ValueStart)),
                FirstBulletIndex(plain, filesLabel.ValueStart)));
            files.AddRange(ArbiterPathRule.ExtractPaths(plain[filesLabel.ValueStart..paragraphEnd]));
            files.AddRange(HarvestBulletRun(plain, paragraphEnd));
        }
        else if (isSpec)
        {
            files.AddRange(ArbiterPathRule.ExtractPaths(plain));
        }

        List<string> dependsTokens = [];
        LabelMatch? dependsLabel = labels.FirstOrDefault(label => label.Family == LabelFamily.Depends);
        if (dependsLabel is not null)
        {
            // The bold boundary is filtered against the value start: the label's own
            // closing `**` produces a bold start immediately before the colon, which is
            // inside the label and must not end its own value region.
            int end = RegionEnd(plain, dependsLabel.ValueStart, Math.Min(
                Math.Min(NextLabelStart(labels, dependsLabel.Start), NextBoldStart(boldStarts, dependsLabel.ValueStart)),
                DoubleNewlineIndex(plain, dependsLabel.ValueStart)));
            string region = plain[dependsLabel.ValueStart..end].Trim().TrimEnd('.');
            if (!region.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                IEnumerable<string> tokens = isSpec
                    ? SpecDependencyTokenRegex().Matches(region).Select(match => match.Groups["id"].Value)
                    : PlanDependencyTokenRegex().Matches(region).Select(match => match.Value);
                foreach (string token in tokens)
                {
                    if (!dependsTokens.Contains(token))
                    {
                        dependsTokens.Add(token);
                    }
                }
            }
        }

        List<string> skills = [];
        foreach (LabelMatch label in labels.Where(label => label.Family == LabelFamily.Skills))
        {
            int end = RegionEnd(plain, label.ValueStart, Math.Min(NextLabelStart(labels, label.Start), DoubleNewlineIndex(plain, label.ValueStart)));
            foreach (Match match in BacktickSpanRegex().Matches(plain[label.ValueStart..end]))
            {
                string name = match.Groups[1].Value.Trim();
                if (name.Length > 0 && !skills.Contains(name))
                {
                    skills.Add(name);
                }
            }
        }

        return (files, dependsTokens, skills);
    }

    /// <summary>
    /// Consumes the bullet block that follows a Files label: bullet lines, their indented
    /// continuations, and blank lines between bullets, until a non-bullet paragraph, a
    /// heading, or the next label. The archived plans separate the label from its bullets
    /// with a blank line, and what follows the list — <c>**Objective:**</c> prose with
    /// its own bullets — is not the task's file scope.
    /// </summary>
    private static IEnumerable<string> HarvestBulletRun(string plain, int from)
    {
        string[] lines = plain[from..].Split('\n');
        int index = 0;
        while (index < lines.Length && lines[index].Trim().Length == 0)
        {
            index++;
        }

        List<string> run = [];
        while (index < lines.Length)
        {
            string line = lines[index];
            bool isBlank = line.Trim().Length == 0;
            if (isBlank)
            {
                int next = index + 1;
                while (next < lines.Length && lines[next].Trim().Length == 0)
                {
                    next++;
                }

                if (next < lines.Length && (BulletLineRegex().IsMatch(lines[next]) || lines[next].StartsWith(' ')))
                {
                    run.Add(line);
                    index++;
                    continue;
                }

                break;
            }

            if (BulletLineRegex().IsMatch(line))
            {
                if (ContainsLabel(line))
                {
                    break;
                }

                run.Add(line);
                index++;
                continue;
            }

            if (line.StartsWith(' '))
            {
                run.Add(line);
                index++;
                continue;
            }

            break;
        }

        return ArbiterPathRule.ExtractPaths(string.Join("\n", run));
    }

    private static List<LabelMatch> CollectLabels(string plain)
    {
        List<LabelMatch> labels = [];
        CollectLabelsOf(plain, FilesLabelRegex(), LabelFamily.Files, labels);
        CollectLabelsOf(plain, DependsLabelRegex(), LabelFamily.Depends, labels);
        CollectLabelsOf(plain, SkillsLabelRegex(), LabelFamily.Skills, labels);
        return [.. labels.OrderBy(label => label.Start)];
    }

    private static void CollectLabelsOf(string plain, Regex regex, LabelFamily family, List<LabelMatch> labels)
    {
        foreach (Match match in regex.Matches(plain))
        {
            labels.Add(new LabelMatch(family, match.Index, match.Index + match.Length));
        }
    }

    private static bool ContainsLabel(string line) =>
        FilesLabelRegex().IsMatch(line) || DependsLabelRegex().IsMatch(line) || SkillsLabelRegex().IsMatch(line);

    private static int NextLabelStart(List<LabelMatch> labels, int after) =>
        labels.Where(label => label.Start > after).Select(label => (int?)label.Start).FirstOrDefault() ?? int.MaxValue;

    private static int NextBoldStart(List<int> boldStarts, int after) =>
        boldStarts.Where(start => start > after).DefaultIfEmpty(int.MaxValue).Min();

    // Region boundaries report "unbounded" as int.MaxValue and a boundary may sit
    // inside the label itself; every slice end is clamped into [valueStart, plain.Length].
    private static int RegionEnd(string plain, int valueStart, int boundary) =>
        Math.Min(Math.Max(Math.Min(boundary, plain.Length), valueStart), plain.Length);

    private static int DoubleNewlineIndex(string plain, int from)
    {
        int index = plain.IndexOf("\n\n", from, StringComparison.Ordinal);
        return index < 0 ? int.MaxValue : index;
    }

    private static int FirstBulletIndex(string plain, int from)
    {
        Match match = BulletLineRegex().Match(plain, from);
        return match.Success ? match.Index : int.MaxValue;
    }

    private static List<string> HeadingSkills(string headingText)
    {
        Match suffix = TrailingSkillSuffixRegex().Match(headingText);
        if (!suffix.Success)
        {
            return [];
        }

        return [.. suffix.Groups["inner"].Value
            .Split(SkillSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => SkillNameRegex().IsMatch(name))];
    }

    private static IReadOnlyList<PlanContractRow> ReadContractRows(string text, MarkdownDocument document, List<HeadingLine> headings)
    {
        HeadingLine? contractHeading = headings.FirstOrDefault(heading => heading.Level == 2 && IsContractHeading(heading.Text));
        if (contractHeading is null)
        {
            return [];
        }

        int end = SectionEnd(headings, contractHeading, text.Length);
        Table? table = document.Descendants<Table>()
            .Where(candidate => candidate.Span.Start >= contractHeading.BodyStart && candidate.Span.Start < end)
            .OrderBy(candidate => candidate.Span.Start)
            .FirstOrDefault();
        if (table is null)
        {
            return [];
        }

        List<TableRow> rows = [.. table.Descendants<TableRow>()];
        if (rows.Count == 0)
        {
            return [];
        }

        List<string> headers = [.. rows[0].Descendants<TableCell>().Select(FlattenCell)];
        List<PlanContractRow> result = [];
        foreach (TableRow row in rows.Skip(1))
        {
            List<string> cells = [.. row.Descendants<TableCell>().Select(FlattenCell)];
            string taskId = cells.Count > 0 ? cells[0].Trim() : string.Empty;
            if (!(PlanTaskIdRegex().IsMatch(taskId) || SpecTaskIdRegex().IsMatch(taskId)))
            {
                continue;
            }

            string? testProject = null;
            string? runner = null;
            string? behaviour = null;
            string? red = null;
            string? green = null;
            for (int index = 1; index < cells.Count && index < headers.Count; index++)
            {
                string value = cells[index].Trim();
                switch (MapHeader(headers[index]))
                {
                    case 0: testProject = value; break;
                    case 1: runner = value; break;
                    case 2: behaviour = value; break;
                    case 3: red = value; break;
                    case 4: green = value; break;
                }
            }

            result.Add(new PlanContractRow(taskId, testProject, runner, behaviour, red, green));
        }

        return result;
    }

    private static int MapHeader(string header)
    {
        if (header.Contains("test project or file", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (header.Contains("runner filter", StringComparison.OrdinalIgnoreCase) ||
            header.Contains("runner command", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (header.Contains("observable behaviour", StringComparison.OrdinalIgnoreCase) ||
            header.Contains("observable behavior", StringComparison.OrdinalIgnoreCase) ||
            header.Contains("behaviour", StringComparison.OrdinalIgnoreCase) ||
            header.Contains("behavior", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (header.Contains("red evidence", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (header.Contains("green acceptance", StringComparison.OrdinalIgnoreCase))
        {
            return 4;
        }

        return -1;
    }

    private static string FlattenCell(TableCell cell) =>
        FlattenInlines(cell.Descendants<ParagraphBlock>().FirstOrDefault()?.Inline);

    private static bool IsTasksHeading(string text)
    {
        string normalized = NormalizeHeadingText(text);
        return normalized.Equals("tasks", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("implementation tasks", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsContractHeading(string text)
    {
        string normalized = NormalizeHeadingText(text);
        return normalized.Contains("test contract", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("verification contract", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOutOfScopeHeading(string text)
    {
        string normalized = NormalizeHeadingText(text);
        return normalized.Equals("out of scope", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("out of scope ", StringComparison.OrdinalIgnoreCase);
    }

    // Headings such as `## Out-of-scope boundaries` occur in the archive; hyphens
    // normalize to spaces so both spellings answer the same section. Casing is left
    // intact and the comparisons are OrdinalIgnoreCase.
    private static string NormalizeHeadingText(string text) =>
        WhitespaceRegex().Replace(text.Replace('-', ' '), " ").Trim();

    private static int SectionEnd(List<HeadingLine> headings, HeadingLine heading, int eof) =>
        headings.Where(candidate => candidate.Start > heading.Start && candidate.Level <= heading.Level)
            .Select(candidate => candidate.Start)
            .Concat([eof])
            .Min();

    private static int BodyStart(string text, int headingSpanEnd)
    {
        int newline = text.IndexOf('\n', Math.Min(headingSpanEnd, text.Length));
        return newline < 0 ? text.Length : newline + 1;
    }

    /// <summary>
    /// Removes <c>**</c> emphasis delimiters and reports where each removed pair opened,
    /// in plain-text coordinates, so a Depends label's value can stop at the next bold
    /// label (the archived mcp plan separates its <c>**depends-on**</c> and
    /// <c>**concurrency**</c> lines inside one paragraph).
    /// </summary>
    private static (string Plain, List<int> BoldStarts) StripEmphasis(string text)
    {
        StringBuilder plain = new(text.Length);
        List<int> boldStarts = [];
        int index = 0;
        while (index < text.Length)
        {
            if (index + 1 < text.Length && text[index] == '*' && text[index + 1] == '*')
            {
                boldStarts.Add(plain.Length);
                index += 2;
                continue;
            }

            plain.Append(text[index]);
            index++;
        }

        return (plain.ToString(), boldStarts);
    }

    /// <summary>Flattens a Markdig inline chain to text, keeping code-span contents and dropping emphasis delimiters.</summary>
    private static string FlattenInlines(Inline? root)
    {
        StringBuilder builder = new();
        AppendInlineChain(builder, root);
        return builder.ToString();
    }

    private static void AppendInlineChain(StringBuilder builder, Inline? start)
    {
        for (Inline? node = start; node is not null; node = node.NextSibling)
        {
            switch (node)
            {
                case LiteralInline literal:
                    builder.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    builder.Append(code.Content);
                    break;
                case LineBreakInline:
                    builder.Append(' ');
                    break;
                default:
                    if (node is ContainerInline container)
                    {
                        AppendInlineChain(builder, container.FirstChild);
                    }

                    break;
            }
        }
    }

    private sealed record HeadingLine(int Level, string Text, int Start, int BodyStart);

    private sealed record LabelMatch(LabelFamily Family, int Start, int ValueStart);

    private enum LabelFamily
    {
        Files,
        Depends,
        Skills
    }

    private sealed record TaskBuild(string Id, string Title, string Text, bool Checked, int Position)
    {
        public List<string> Files { get; init; } = [];

        public List<string> DependsTokens { get; init; } = [];

        public List<string> Skills { get; init; } = [];
    }
}

/// <summary>
/// The plan path rule (design §6): a backticked span is a path when it contains
/// <c>/</c> or ends in a file extension and contains no spaces; a bare file name after
/// a path in the same bullet inherits that path's directory; a trailing <c>/</c>
/// becomes <c>dir/**</c>; a <c>&lt;placeholder&gt;</c> segment becomes <c>*</c>; other
/// spans — type names, flags — are ignored. Internal because task 1.6 applies the same
/// rule to delegation prompts.
/// </summary>
internal static partial class ArbiterPathRule
{
    /// <summary>Extracts the paths of a text region, bullet by bullet, in declaration order.</summary>
    public static IReadOnlyList<string> ExtractPaths(string text)
    {
        List<string> paths = [];
        foreach (string unit in SplitUnits(text))
        {
            string directory = string.Empty;
            foreach (Match match in BacktickSpan().Matches(unit))
            {
                string span = match.Groups[1].Value;
                if (string.IsNullOrWhiteSpace(span) || !IsPathSpan(span))
                {
                    continue;
                }

                string normalized = NormalizeSpan(span);
                string resolved = span.Contains('/', StringComparison.Ordinal) || directory.Length == 0 ? normalized : directory + normalized;
                if (resolved.Length > 0 && !paths.Contains(resolved))
                {
                    paths.Add(resolved);
                }

                if (span.Contains('/', StringComparison.Ordinal))
                {
                    directory = span[..(span.LastIndexOf('/') + 1)];
                }
            }
        }

        return paths;
    }

    /// <summary>Decides whether a backticked span is a path under the rule.</summary>
    internal static bool IsPathSpan(string span) =>
        !HasWhitespace(span) && (span.Contains('/', StringComparison.Ordinal) || FileExtensionRegex().IsMatch(span));

    /// <summary>Applies the trailing-slash and placeholder-segment rewrites.</summary>
    internal static string NormalizeSpan(string span)
    {
        string normalized = PlaceholderSegmentRegex().Replace(span, "*");
        if (normalized.Length > 1 && normalized.EndsWith('/'))
        {
            normalized = normalized.TrimEnd('/') + "/**";
        }

        return normalized;
    }

    private static bool HasWhitespace(string span) => span.AsSpan().IndexOfAny(' ', '\t') >= 0;

    /// <summary>
    /// Splits a region into inheritance units: each bullet starts one and keeps its
    /// indented continuations; other text is split per blank-line-separated paragraph,
    /// so a bare file name never inherits across a paragraph boundary.
    /// </summary>
    private static IEnumerable<string> SplitUnits(string text)
    {
        List<string> units = [];
        StringBuilder current = new();
        bool inBullet = false;
        bool previousBlank = true;
        foreach (string line in text.Split('\n'))
        {
            bool isBlank = line.Trim().Length == 0;
            bool isBullet = !isBlank && BulletLineRegex().IsMatch(line);
            if (isBullet)
            {
                Flush(units, current);
                current.Clear();
                current.Append(line);
                inBullet = true;
                previousBlank = false;
            }
            else if (isBlank)
            {
                Flush(units, current);
                current.Clear();
                inBullet = false;
                previousBlank = true;
            }
            else if (inBullet && (line.StartsWith(' ') || line.StartsWith('\t')))
            {
                current.Append('\n').Append(line);
                previousBlank = false;
            }
            else if (inBullet || previousBlank)
            {
                Flush(units, current);
                current.Clear();
                current.Append(line);
                inBullet = false;
                previousBlank = false;
            }
            else
            {
                current.Append('\n').Append(line);
                previousBlank = false;
            }
        }

        Flush(units, current);
        return units;
    }

    private static void Flush(List<string> units, StringBuilder current)
    {
        if (current.Length > 0 && current.ToString().AsSpan().Trim().Length > 0)
        {
            units.Add(current.ToString());
        }
    }

    [GeneratedRegex(@"`([^`\n]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex BacktickSpan();

    [GeneratedRegex(@"^[ \t]*-[ \t]+", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex BulletLineRegex();

    [GeneratedRegex(@"<[^<>\n]*>", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderSegmentRegex();

    [GeneratedRegex(@"\.[A-Za-z][A-Za-z0-9]{0,7}$", RegexOptions.CultureInvariant)]
    private static partial Regex FileExtensionRegex();
}
