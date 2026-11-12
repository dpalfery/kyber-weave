```yaml
- id: performance/net-changed-lines-quadratic
  lens: performance
  severity: major
  confidence: 8
  file: src/KyberWeave.Core/Arbiter/Facts/GitFacts.cs
  line: 650
  excerpt: |
    List<string> unmatchedRemoved = [.. removed];
    List<string> net = [];
    foreach (string line in added)
    {
        if (!unmatchedRemoved.Remove(line))
  evidence: >
    List<T>.Remove is a linear scan. NetChangedLines runs once per hunk inside ParseChangeSet, which
    runs on every hook event whose rule asks for a change fact (HookGitFacts.cs:138). With --unified=0
    a rewritten file is a single hunk holding all its removed and all its added lines.
  failure_scenario: >
    A branch regenerates or reformats a 100k-line file (lockfile, generated code, line-ending
    rewrite), so git prints one hunk with about 100k removed and 100k added lines. Lines that differ
    never match, so every Remove scans the whole removed list: about 10^10 string compares. That
    happens synchronously in the dispatch hook for each lens.spawn and lens.returned event, and the
    hook stalls or times out. The growth dimension is lines per hunk, squared.
  guidance: [R8]
  suggestion: >
    Build a Dictionary<string,int> of removed-line counts and decrement it while walking the added
    lines. Rebuild the leftover removed lines in order, so the work is linear.

- id: performance/untracked-files-read-whole
  lens: performance
  severity: major
  confidence: 7
  file: src/KyberWeave.Core/Arbiter/Facts/GitFacts.cs
  line: 301
  excerpt: |
    lines = File.ReadAllLines(Path.Combine(root, path));
  evidence: >
    GetChangeSet reads every untracked, non-ignored file in full, with no size cap or binary check.
    It does so on every hook event that needs a change fact, and CommentSyntax then walks every line.
    The catch clause covers only IOException, UnauthorizedAccessException and ArgumentException, so
    an OutOfMemoryException from a very large file is not caught.
  failure_scenario: >
    The working tree holds untracked, un-ignored artifacts, for example a 1 GB dump or log, a
    vendored data file, or a tool output directory. Every lens.spawn or lens.returned event then
    loads each of them into a string[] and builds a hunk with millions of lines, once per lens in
    the fan-out. The hook is slow or runs out of memory. The growth dimension is the total size of
    untracked files times the number of lenses.
  guidance: [R8]
  suggestion: >
    Cap the bytes read per untracked file. Treat a file over the cap, or one that contains NUL, as
    a single opaque hunk with no comment-only candidate lines. Alternatively get the untracked line
    counts without loading the content.

- id: performance/comment-only-paths-quadratic
  lens: performance
  severity: minor
  confidence: 7
  file: src/KyberWeave.Arbiter/Hooks/HookGitFacts.cs
  line: 169
  excerpt: |
    .Where(path => change.Hunks
        .Where(measured => string.Equals(measured.Hunk.Path, path, StringComparison.Ordinal))
        .All(measured => CommentSyntax.IsCommentOnly(path, measured.ChangedLines)))
  evidence: >
    For each distinct changed path the whole hunk list is rescanned (paths times hunks). The same
    full rescan is repeated in WithFindingChangedLines at line 220. Both run on every hook event
    that needs the facts.
  failure_scenario: >
    A 5,000-file refactor produces about 20,000 hunks. That is about 10^8 ordinal string compares
    per event, repeated for each lens spawn and return, where one pass would do. The growth
    dimension is changed paths times hunks.
  guidance: [R8]
  suggestion: >
    Group the hunks by path once with ToLookup or GroupBy, then evaluate each group. Reuse the
    grouping in WithFindingChangedLines.

- id: prior-art/changed-hunk-vs-hunk-range
  lens: prior-art
  severity: minor
  confidence: 7
  file: src/KyberWeave.Core/Arbiter/Facts/GitFacts.cs
  line: 24
  excerpt: |
    public sealed record ChangedHunk(string Path, int OldStart, int OldCount, int Start, int Count);
  evidence: >
    The same file already declares `public sealed record HunkRange(string Path, int StartLine, int LineCount);`
    (GitFacts.cs:11), read by `GetHunkRanges` (:197) with `ParseHunkRanges` (:716) and `TryParseNewSide` (:805).
    The diff adds a second per-hunk range record, read by `GetChangedHunks` and `GetChangeSet` with
    `ParseChangeSet` and `TryParseHunkHeader`. HunkRange is a strict subset of ChangedHunk.
    `GetHunkRanges` is still live with 2 callers. It runs `git diff --unified=0 <base>` without
    `-M`, `--no-ext-diff` or `core.quotePath=false`, and it skips untracked files.
  failure_scenario: >
    Two callers measure "the hunks against the base" with different rules. A change to hunk-header
    parsing, such as the bare `+c` one-line rule at GitFacts.cs:818 and its copy in TryParseRange,
    or to rename or untracked handling, must be made at both sites. Nothing links them. The
    GetHunkRanges callers would see a renamed file as a delete plus an add, while review.changed-hunks
    sees one entry, and the tests in ArbiterReadersTests cover only the old path.
  guidance: [R9, §4]
  suggestion: >
    Have GetHunkRanges project from GetChangeSet, or retire it in favour of ChangedHunk. Then there
    is one header parser and one definition of the change.

- id: prior-art/model-targets-filename-three-sites
  lens: prior-art
  severity: minor
  confidence: 7
  file: src/KyberWeave.Cli/Commands/Arbiter/ArbiterDoctorCommand.cs
  line: 112
  excerpt: |
    string targetsPath = Path.Combine(arbiterDirectory, $"model-targets.{token}.json");
  evidence: >
    The `model-targets.<harness>.json` name is built by hand in three places:
    ArbiterModelTargets.TryRead (`Path.Combine(arbiterDirectory, $"model-targets.{harness}.json")`,
    ArbiterModelTargets.cs:46), ArbiterModelTargetsFile.ProjectRelativePath and GlobalRelativePath
    (Core.Squad), and this doctor line. The doctor line sits next to a TryRead call at line 113 on
    the same directory and token.
  failure_scenario: >
    Renaming or versioning the file (for example `model-targets.v2.<harness>.json`) means editing the
    reader, the writer's two relative-path helpers, and the doctor's message path. If the doctor
    copy is missed, it reports a "missing" file at the wrong path while TryRead reads the new one.
  guidance: [R9]
  suggestion: >
    Expose one file-name helper on ArbiterModelTargets, in Core, which Core.Squad already
    references. Have TryRead, the two relative-path helpers and the doctor all call it.
```
REVIEW.md sections checked: [Ground rules, R8, R9, §4, §13]
