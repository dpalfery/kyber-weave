using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>Shows the effective provider, model, endpoint origin and key presence.</summary>
/// <remarks>
/// The key's value is never shown: the command prints only whether one resolved
/// for the endpoint origin. A remote provider with no resolving key raises
/// <c>KW-ARB-KEY-001</c> as a warning, matching <c>doctor</c>.
/// </remarks>
public sealed class ArbiterStatusCommand : Command<ArbiterSettings>
{
    /// <summary>A remote provider is configured, and no key resolves for its origin.</summary>
    public const string KeyMissing = "KW-ARB-KEY-001";

    /// <summary>
    /// Prints provider, model, endpoint origin and whether a key was found. Writes
    /// nothing. Exits 1 only when the configuration itself fails to load; a missing
    /// key is a warning and exits 0.
    /// </summary>
    public override int Execute(CommandContext context, ArbiterSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(Console.Out),
        });
        AnsiConsole.Profile.Width = int.MaxValue;
        DiagnosticReport report = new();
        if (!ArbiterCommandComposition.TryResolveConfig(settings, report, out KyberWeaveConfig config, out _))
        {
            CommandHelpers.Finish(report, settings, "arbiter status", "Rule");
            return 1;
        }

        ArbiterProviderConfig provider = config.Arbiter.Provider;
        string? origin = ArbiterKeyResolver.GetOrigin(provider.Endpoint);
        bool remote = provider.Kind != ArbiterProviderKind.None
            && !ArbiterKeyResolver.IsLoopbackEndpoint(provider.Endpoint);
        string? key = remote ? ArbiterCommandComposition.TryResolveKey(config, provider.Endpoint) : null;
        IReadOnlyList<string> globalTargets = UserHostConfig.ReadGlobalTargets(ArbiterCommandComposition.HomeDirectory());
        bool projectEnabled = config.Arbiter.Enabled;

        AnsiConsole.MarkupLine("[bold]Arbiter status[/]");
        AnsiConsole.MarkupLine($"  Provider: [bold]{Markup.Escape(KindName(provider.Kind))}[/]");
        AnsiConsole.MarkupLine($"  Model: [bold]{Markup.Escape(provider.Model)}[/]");
        AnsiConsole.MarkupLine($"  Endpoint: [bold]{Markup.Escape(provider.Endpoint)}[/]");
        AnsiConsole.MarkupLine($"  Origin: [bold]{Markup.Escape(origin ?? "none")}[/]");
        if (!remote)
        {
            AnsiConsole.MarkupLine("  Key: [grey]not needed (none or loopback provider).[/]");
        }
        else if (key is null)
        {
            AnsiConsole.MarkupLine("  Key: [yellow]missing.[/]");
            report.Add(new Diagnostic(
                KeyMissing,
                Severity.Warning,
                $"A remote provider is configured, and no key resolves for origin '{origin}'.",
                "arbiter.provider",
                Hint: $"Run 'arbiter setup' or export {ArbiterKeyResolver.EnvVarName}."));
        }
        else
        {
            AnsiConsole.MarkupLine("  Key: [green]found.[/]");
        }

        WriteGlobalScope(globalTargets);
        WriteProjectScope(projectEnabled, globalTargets.Count > 0);

        CommandHelpers.Finish(report, settings, "arbiter status", "Rule");
        return report.HasErrors ? 1 : 0;
    }

    /// <summary>Runs the command without cancellation; the entry point tests use.</summary>
    public int Execute(CommandContext context, ArbiterSettings settings) =>
        Execute(context, settings, CancellationToken.None);

    private static void WriteGlobalScope(IReadOnlyList<string> targets)
    {
        AnsiConsole.MarkupLine("[bold]GLOBAL scope[/]");
        if (targets.Count == 0)
        {
            AnsiConsole.MarkupLine("  [grey]no global targets configured.[/]");
            return;
        }

        foreach (string token in targets)
        {
            string reason = GlobalReason(token);
            string label = TargetDisplayName(token);
            AnsiConsole.MarkupLine($"  {Markup.Escape(label)}: {Markup.Escape(reason)}");
        }
    }

    private static void WriteProjectScope(bool enabled, bool projectOverridesGlobal)
    {
        AnsiConsole.MarkupLine("[bold]PROJECT scope[/]");
        if (enabled)
        {
            AnsiConsole.MarkupLine("  project overrides global");
            AnsiConsole.MarkupLine("  arbiter.enabled: true");
            return;
        }

        if (projectOverridesGlobal)
        {
            AnsiConsole.MarkupLine("  project does not override global");
            return;
        }

        AnsiConsole.MarkupLine("  no project override");
    }

    private static string GlobalReason(string token)
    {
        if (!SquadTargetCatalog.TryResolve(token, out SquadTarget target))
        {
            return "enforced-global";
        }

        IReadOnlyList<SquadDegradationRecord> records = ArbiterHookWiring.TargetDegradations(
            [target],
            SquadDeploymentScope.Global);
        return records.Count == 0 ? "enforced-global" : (records[0].Details ?? "enforced-global");
    }

    private static string TargetDisplayName(string token)
    {
        if (!SquadTargetCatalog.TryResolve(token, out SquadTarget target))
        {
            return token;
        }

        return target switch
        {
            SquadTarget.Claude => "Claude",
            SquadTarget.Codex => "Codex",
            SquadTarget.Copilot => "Copilot",
            SquadTarget.Cursor => "Cursor",
            SquadTarget.OpenCode => "OpenCode",
            SquadTarget.Pi => "Pi",
            SquadTarget.Warp => "Warp",
            SquadTarget.ZCode => "ZCode",
            SquadTarget.Kilo => "Kilo",
            SquadTarget.Antigravity => "Antigravity",
            SquadTarget.Factory => "Factory",
            SquadTarget.Devin => "Devin",
            SquadTarget.CommandCode => "CommandCode",
            _ => token,
        };
    }

    private static string KindName(ArbiterProviderKind kind) =>
        kind == ArbiterProviderKind.Systemone ? "systemone" : "none";
}
