using System.ComponentModel;
using KyberWeave.Cli.Rendering;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Scaffolding;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Docs;

/// <summary>Settings for <c>kyber-weave docs onboarding &lt;ANSWER&gt;</c>.</summary>
public sealed class DocsOnboardingSettings : CommandSettings
{
    /// <summary>The answer to record: <c>done</c> or <c>declined</c>.</summary>
    [CommandArgument(0, "<ANSWER>")]
    [Description(
        "The answer to record: 'done' once the onboarding skill has been applied, or 'declined' to record " +
        "that this repository chose not to onboard. Either one ends the question, so a later run does not " +
        "stop to ask it again.")]
    public string Answer { get; set; } = string.Empty;

    /// <summary>The repository root whose configuration is edited.</summary>
    [CommandArgument(1, "[path]")]
    [Description("Repository root holding .kyber-weave/kyber-weave.yml. Defaults to the current directory.")]
    public string Path { get; set; } = ".";
}

/// <summary>
/// Records the answer to the onboarding question in <c>.kyber-weave/kyber-weave.yml</c>, and refreshes the
/// Config Reg block in <c>AGENTS.md</c> so an agent can read it.
/// </summary>
/// <remarks>
/// <para>
/// The value is written through <see cref="HostConfigYaml"/> rather than by rewriting the document: the host
/// configuration is hand-authored and carries the operator's comments and their reasons for each choice, and a
/// round trip through a YAML emitter discards them.
/// </para>
/// <para>
/// The block is re-spliced because <c>onboarding</c> is a published property: a repository that recorded its
/// answer would otherwise fail <c>docs validate</c> over a generated block that no operator is meant to edit,
/// and an agent reading the registry would not see the answer at all.
/// </para>
/// <para>
/// Exit codes: <c>0</c> the value is recorded, <c>2</c> an answer that is not one of the two (nothing is
/// written), <c>1</c> an operational failure — no configuration to edit, or a file that could not be read or
/// written.
/// </para>
/// </remarks>
public sealed class DocsOnboardingCommand : Command<DocsOnboardingSettings>
{
    /// <summary>The verb as the operator types it, for messages.</summary>
    private const string CommandName = "kyber-weave docs onboarding";

    /// <summary>The repository root file the generated registry block lives in.</summary>
    private const string AgentsFile = "AGENTS.md";

    /// <summary>An answer outside the two the setting accepts; nothing is written.</summary>
    private const int RefusedInput = 2;

    /// <summary>A file this command could not read, write, or find.</summary>
    private const int Failed = 1;

    /// <summary>Records the answer, then refreshes the generated registry block.</summary>
    /// <param name="context">The Spectre command context.</param>
    /// <param name="settings">The answer and the repository root.</param>
    /// <param name="cancellationToken">Unused; nothing here waits on anything.</param>
    public override int Execute(
        CommandContext context,
        DocsOnboardingSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string answer = settings.Answer?.Trim() ?? string.Empty;
        if (!ConfigRegConfig.OnboardingAnswers.Contains(answer, StringComparer.Ordinal))
        {
            AnsiConsole.MarkupLine(
                $"[red]{Markup.Escape(CommandName)}: error:[/] '{Markup.Escape(answer)}' is not an " +
                $"onboarding answer. Use '{ConfigRegConfig.OnboardingDone}' once the onboarding skill has " +
                $"been applied, or '{ConfigRegConfig.OnboardingDeclined}' to record that this repository " +
                "chose not to onboard. Nothing was written.");
            return RefusedInput;
        }

        string root = Path.GetFullPath(settings.Path);
        DiagnosticReport report = new();
        if (!CommandHelpers.TryLoadConfig(root, null, report, out _, out string? configPath))
        {
            ReportRenderer.Render(report, OutputFormat.Table, "docs onboarding", "kyber-weave.yml");
            return Failed;
        }

        if (configPath is null)
        {
            AnsiConsole.MarkupLine(
                $"[red]{Markup.Escape(CommandName)}: error:[/] no host configuration was found under " +
                $"'{Markup.Escape(root)}', so there is nowhere to record the answer.");
            AnsiConsole.MarkupLine(
                "[grey]→ run 'kyber-weave docs init .' to create .kyber-weave/kyber-weave.yml. A repository " +
                "without one is never stopped for onboarding, so nothing is missing until it is configured.[/]");
            return Failed;
        }

        int written = RecordAnswer(configPath, answer);
        if (written != 0)
            return written;

        AnsiConsole.MarkupLine(
            $"onboarding: [bold]{Markup.Escape(answer)}[/] recorded in " +
            $"[grey]{Markup.Escape(Path.GetRelativePath(root, configPath))}[/]");

        return RefreshRegistry(root);
    }

    /// <summary>Writes the answer into the configuration file, and reports whether it changed.</summary>
    /// <returns><c>0</c> when the file now holds the answer, or <see cref="Failed"/>.</returns>
    private static int RecordAnswer(string configPath, string answer)
    {
        string existing;
        try
        {
            existing = File.ReadAllText(configPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine(
                $"[red]{Markup.Escape(CommandName)}: error:[/] '{Markup.Escape(configPath)}' could not be " +
                $"read: {Markup.Escape(ex.Message)}");
            AnsiConsole.MarkupLine("[grey]→ check that the file is readable, then run the command again.[/]");
            return Failed;
        }

        string updated = HostConfigYaml.WithOnboarding(existing, answer);
        if (string.Equals(existing, updated, StringComparison.Ordinal))
            return 0;

        try
        {
            File.WriteAllText(configPath, updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine(
                $"[red]{Markup.Escape(CommandName)}: error:[/] '{Markup.Escape(configPath)}' could not be " +
                $"written: {Markup.Escape(ex.Message)}");
            AnsiConsole.MarkupLine("[grey]→ check that the file is writable, then run the command again.[/]");
            return Failed;
        }

        return 0;
    }

    /// <summary>
    /// Re-renders the Config Reg block from the configuration the file now holds.
    /// </summary>
    /// <remarks>
    /// Only a file that exists is touched: creating <c>AGENTS.md</c> is <c>docs init</c>'s job, and a command
    /// asked to record one value should not hand an operator a scaffold they did not ask for. A block that is
    /// absent from an existing file is appended, which is what <c>docs init</c> does with the same renderer.
    /// </remarks>
    private static int RefreshRegistry(string root)
    {
        string agentsPath = Path.Combine(root, AgentsFile);
        if (!File.Exists(agentsPath))
        {
            AnsiConsole.MarkupLine(
                $"[yellow]warn[/] {AgentsFile} does not exist, so no Config Reg block was written. Agents " +
                "read the setting from that block: run 'kyber-weave docs init .' to create it.");
            return 0;
        }

        KyberWeaveConfigLoadResult loaded = KyberWeaveConfigLoader.TryLoad(root);
        if (!loaded.Success || loaded.Config is null)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]warn[/] the configuration could not be reloaded, so the Config Reg block in " +
                $"{AgentsFile} was left as it was: {Markup.Escape(loaded.Error ?? "unknown error")}");
            return 0;
        }

        string existing;
        string updated;
        try
        {
            existing = File.ReadAllText(agentsPath);
            KyberWeaveConfig config = loaded.Config;
            updated = ConfigRegMarkdown.Splice(
                existing,
                ConfigRegMarkdown.Render(
                    config.ConfigReg.Resolve(config.Ontology),
                    config.ConfigReg.Autonomy));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            AnsiConsole.MarkupLine(
                $"[red]{Markup.Escape(CommandName)}: error:[/] the answer was recorded, but the Config Reg " +
                $"block in {AgentsFile} could not be refreshed: {Markup.Escape(ex.Message)}");
            AnsiConsole.MarkupLine(
                "[grey]→ fix what the message names and run 'kyber-weave docs init .', which rewrites the " +
                "block from the configuration.[/]");
            return Failed;
        }

        if (string.Equals(existing, updated, StringComparison.Ordinal))
            return 0;

        try
        {
            File.WriteAllText(agentsPath, updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine(
                $"[red]{Markup.Escape(CommandName)}: error:[/] the answer was recorded, but {AgentsFile} " +
                $"could not be written: {Markup.Escape(ex.Message)}");
            AnsiConsole.MarkupLine(
                "[grey]→ check that the file is writable, then run 'kyber-weave docs init .' to refresh the " +
                "Config Reg block.[/]");
            return Failed;
        }

        AnsiConsole.MarkupLine(
            $"Config Reg block in [grey]{AgentsFile}[/] refreshed, so agents read " +
            $"<{ConfigRegConfig.OnboardingProperty}> from it.");
        return 0;
    }
}
