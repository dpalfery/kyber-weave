namespace KyberWeave.Domain.Hooks;

/// <summary>The built-in hook configuration: C# format checks on, C# targeted tests off.</summary>
public static class HooksDefaults
{
    private static readonly string[] CSharpInclude = ["**/*.cs", "**/*.csproj", "**/*.props"];
    private static readonly string[] BuildOutput = ["**/bin/**", "**/obj/**"];

    /// <summary>Gets the defaults a repository config is merged over.</summary>
    public static HooksConfig Config { get; } = new(
        TimeBudgetSeconds: 30,
        StopOnFirstBlocking: false,
        MaxOutputLines: 30,
        MaxOutputBytes: 3000,
        Rules:
        [
            new HookRule(
                "dotnet-format-whitespace",
                HookRuleKind.Lint,
                CSharpInclude,
                BuildOutput,
                ["dotnet", "format", "{target}", "whitespace", "--verify-no-changes", "--include", "{file}", "-v", "minimal"],
                TimeoutSeconds: 25,
                Blocking: true,
                Enabled: true),
            new HookRule(
                "dotnet-format-style",
                HookRuleKind.Lint,
                CSharpInclude,
                BuildOutput,
                ["dotnet", "format", "{target}", "style", "--verify-no-changes", "--severity", "warn", "--include", "{file}", "-v", "minimal"],
                TimeoutSeconds: 25,
                Blocking: true,
                Enabled: true),
            new HookRule(
                "dotnet-test",
                HookRuleKind.Test,
                ["**/*.cs"],
                BuildOutput,
                ["dotnet", "test", "--project", "{testProject}", "-c", "Release", "--filter-class", "{testClasses}"],
                TimeoutSeconds: 120,
                Blocking: true,
                Enabled: false)
        ],
        Warnings: []);
}
