namespace KyberWeave.Domain.Hooks;

/// <summary>One check the edit hook may run after a file changes.</summary>
/// <param name="Id">Stable name; a rule in the repository config with the same id overrides the built-in one.</param>
/// <param name="Kind">Whether this is a lint or a test rule.</param>
/// <param name="Include">Globs over the repository-relative path; the rule applies when any matches.</param>
/// <param name="Exclude">Globs that remove a path from the rule even when an include matches.</param>
/// <param name="Run">
/// The command as an argument list. Tokens: <c>{file}</c> and <c>{marker}</c> and <c>{project}</c> are
/// repository-relative paths, <c>{root}</c> is the absolute repository root, <c>{testProject}</c> is the test
/// project that holds the covering tests, and a standalone <c>{testClasses}</c> argument expands to one
/// argument per fully qualified test class.
/// </param>
/// <param name="TimeoutSeconds">Longest the command may run.</param>
/// <param name="Blocking">Whether a failure makes the hook exit 2 so the agent sees it.</param>
/// <param name="Enabled">Whether the rule runs at all.</param>
public sealed record HookRule(
    string Id,
    HookRuleKind Kind,
    IReadOnlyList<string> Include,
    IReadOnlyList<string> Exclude,
    IReadOnlyList<string> Run,
    int TimeoutSeconds,
    bool Blocking,
    bool Enabled);
