```yaml
- id: blast-radius-revertibility/comment-only-skips-directive-comments
  lens: blast-radius-revertibility
  severity: major
  confidence: 7
  file: src/KyberWeave.Core/Arbiter/Facts/CommentSyntax.cs
  line: 6492
  excerpt: |
    if (style.LinePrefixes.Any(prefix => rest.StartsWith(prefix, StringComparison.Ordinal)))
    {
        break;
    }
  claim: Any line starting with a comment token counts as a comment, including lines that change behaviour. Examples are Go `//go:build` and `//go:embed`, and a shell shebang `#!/bin/sh`. KW-ARB-LENS-002 then skips six lenses on that change in the default config.
  evidence: |
    The `CStyle` entry (`//`) covers `.go` and `.ts`. `HashStyle` (`#`) covers `.sh`, `.py`, `.yml` and `.rb`. `IsCommentOnlyIn` has no exclusion for directives or shebangs.
    `HookGitFacts.WithChangeFacts` puts any all-comment file into `review.no-code-change-paths`.
    The shipped `KW-ARB-LENS-002` rule in `default-rules.yml` is enabled by default. It maps `no-code-change` to `skip` for correctness, test-adequacy, performance, di-composition, duplicate-implementation and model-placement.
    The same weakness shows up for lines inside multi-line string literals or heredocs. A line there that starts with `#` or `//` is data or script, not a comment.
  failure_scenario: |
    A PR changes only `//go:build linux` to `//go:build !linux` in `foo.go`. The hunk's changed lines are `//` lines, so the file is comment-only.
    The only changed path is therefore in `review.no-code-change-paths`, and LENS-002 returns `skip` for correctness, test-adequacy and performance.
    A change that alters which platforms compile gets no correctness review. The same happens when `#!/bin/bash` is changed to `#!/bin/sh` in a script.
  guidance: [§3, §11]
  suggestion: |
    Treat directive-shaped comment lines as code: `//go:`, `//+build`, `// @ts-`, `#!` on line 1, `# yaml-language-server`, `# -*-`, and `#pragma`-style pragmas.
    Alternatively, keep the comment-only skip shadow-first, per `Shadow`, until the directive list is proven.

- id: blast-radius-revertibility/untracked-files-read-whole-in-hook
  lens: blast-radius-revertibility
  severity: major
  confidence: 7
  file: src/KyberWeave.Core/Arbiter/Facts/GitFacts.cs
  line: 6650
  excerpt: |
    lines = File.ReadAllLines(Path.Combine(root, path));
    ...
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
  claim: |
    Every untracked, non-ignored file is read fully into memory on the hook path. There is no size cap, binary check or timeout. `OutOfMemoryException` is not caught.
    The read now runs on every `lens.spawn` in the default config, because LENS-002 declares `review.no-code-change-paths`.
  evidence: |
    `HookGitFacts.Enrich` calls `GitFacts.GetChangeSet` whenever any of the five change facts is declared (`needsChangeSet`).
    The default `KW-ARB-LENS-002` rule, enabled by default, declares `review.no-code-change-paths`. So this runs for each of the ~6+ lens spawns of one review, repeating `git diff`, `ls-files` and a whole-file read each time.
    The doc comment says "The list is never cut short", so large files are deliberately not skipped. The catch covers only IO and permission errors.
    Per the briefing, an uncaught exception on the hook path becomes KW-ARB-HOOK-001 and fails closed (error, never allow).
  failure_scenario: |
    A developer has a multi-GB untracked, non-ignored file in the checkout, for example a database dump, model weights or a build output not covered by .gitignore.
    Each lens spawn then loads the whole file as a string array. That causes a hook timeout or an OOM crash.
    The dispatch is denied, so no review lens can be spawned until the file is removed or ignored. The error does not name the file.
  guidance: [§3, §6]
  suggestion: |
    Bound the untracked read: check `FileInfo.Length` against a cap, or skip non-text files.
    On exceeding the cap, return null (undecidable, so the lens runs), rather than reading the file.
    Compute the change set once per review rather than once per lens, and include the offending path in any failure message.

- id: supportability/model-targets-rewrite-silently-keeps-damaged-table
  lens: supportability
  severity: minor
  confidence: 7
  file: src/KyberWeave.Cli/Commands/Squad/Infrastructure/SquadPresetFileRewriter.cs
  line: 4704
  excerpt: |
    catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
    {
        return rawBytes;
    }
  claim: |
    A model targets table that does not parse is kept as is while the agent files are rewritten. The exception is dropped with no log, warning or diff row.
    The XML remark says "the table and the agents change together or not at all".
  evidence: |
    `RewriteModelTargetsTable` swallows the exception and returns `rawBytes`.
    The caller still reports success for the preset change.
    `ArbiterModelTargets.TryRead` later treats the same file as absent. The doctor check covers only global scope, via `CheckModelTargets` over `globalTargets`. Project-scope tables get no warning.
  failure_scenario: |
    A project-scope `artifacts/arbiter/model-targets.claude.json` is hand-edited into invalid JSON.
    `squad preset use fast-cheap` succeeds and rewrites the agent model tokens, but the table still carries the old tokens or stays unreadable.
    The Arbiter silently drops model-override decisions for that project, and nothing tells the user which file is wrong.
  guidance: [§6]
  suggestion: |
    On parse failure, surface a warning naming the table path, or regenerate the table from the profiles using the bundled digests.
    Extend the KW-ARB-MODEL-001 doctor check to project-scope tables.
```
REVIEW.md sections checked: [Ground rules, §3, §6, §11]
