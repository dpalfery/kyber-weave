namespace KyberWeave.Domain.Hooks;

/// <summary>The two kinds of check a hook rule runs. Lint rules run before test rules.</summary>
public enum HookRuleKind
{
    /// <summary>A format, style or static-analysis check on the changed file.</summary>
    Lint,

    /// <summary>A targeted test run covering the changed file. Never a whole suite.</summary>
    Test
}
