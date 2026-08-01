using System.Diagnostics;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Processes;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Docs;

/// <summary>
/// Bootstraps a repository into a governable documentation corpus, and deploys the
/// authoring skill that fills in what only judgment can.
/// </summary>
/// <remarks>
/// Skill deployment is delegated to APM rather than reimplemented: APM already resolves
/// harness layouts for every runtime it supports, and a second copy of that mapping here
/// would drift from it. A missing APM is reported with the manual command and does not
/// fail the command — the corpus scaffolding is the part that must succeed.
/// </remarks>
public sealed class DocsInitCommand : Command<DocsInitSettings>
{
    /// <summary>The package APM installs to obtain the authoring skill.</summary>
    private const string SkillPackage = "dpalfery/kyber-weave";

    public override int Execute(CommandContext context, DocsInitSettings settings)
    {
        ScaffoldResult result;
        try
        {
            result = DocsScaffolder.Scaffold(
                settings.Path, settings.DocsRoot, settings.Owner, settings.Force);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // ArgumentException covers a --docs-root that escapes the repository root and
            // an --owner that would inject structure into the emitted YAML or catalog row.
            AnsiConsole.MarkupLine($"[red]Could not scaffold: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        var rootNote = result.DocsRootDetected ? " [grey](detected)[/]" : string.Empty;
        AnsiConsole.MarkupLine($"Documentation root: [bold]{Markup.Escape(result.DocsRoot)}[/]{rootNote}");
        AnsiConsole.WriteLine();

        foreach (var file in result.Files)
        {
            AnsiConsole.MarkupLine(file.Written
                ? $"  [green]created[/]  {Markup.Escape(file.RelativePath)}"
                : $"  [grey]exists[/]   {Markup.Escape(file.RelativePath)} [grey](left alone; --force to overwrite)[/]");
        }

        AnsiConsole.WriteLine();

        if (!settings.NoSkill)
        {
            DeploySkill(settings);
        }

        AnsiConsole.MarkupLine("[bold]Next:[/]");
        AnsiConsole.MarkupLine($"  1. Replace the example row in [grey]{Markup.Escape(result.DocsRoot)}/catalog.md[/] with your real components and owners.");
        AnsiConsole.MarkupLine($"  2. Run [grey]kyber-weave docs validate {Markup.Escape(settings.Path)}[/] to see what the corpus needs.");
        AnsiConsole.MarkupLine("  3. Ask your agent to apply the [grey]kyber-weave-docs[/] skill to the failing documents.");

        return 0;
    }

    private static void DeploySkill(DocsInitSettings settings)
    {
        var startInfo = new ProcessStartInfo("apm")
        {
            WorkingDirectory = Path.GetFullPath(settings.Path),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        // Arguments are passed as a list, never through a shell, so a target string
        // cannot be reinterpreted as anything but an argument.
        startInfo.ArgumentList.Add("install");
        startInfo.ArgumentList.Add(SkillPackage);
        startInfo.ArgumentList.Add("--target");
        startInfo.ArgumentList.Add(settings.Target);

        var command = $"apm install {SkillPackage} --target {settings.Target}";

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                SkillUnavailable(command, "the 'apm' process could not be started");
                return;
            }

            var result = ProcessRunner.ReadToEnd(process);

            if (result.ExitCode == 0)
            {
                AnsiConsole.MarkupLine($"  [green]deployed[/] kyber-weave-docs skill → target [bold]{Markup.Escape(settings.Target)}[/]");
                AnsiConsole.WriteLine();
                return;
            }

            var error = result.StandardError.Trim();
            var detail = error.Length > 0 ? error : result.StandardOutput.Trim();
            SkillUnavailable(command, $"apm exited {result.ExitCode}: {detail}");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            SkillUnavailable(command, "APM is not installed or not on PATH");
        }
    }

    /// <summary>
    /// Reports that the skill was not deployed and how to do it by hand. Deliberately not
    /// a failure: the corpus was scaffolded, and the skill is an accelerator for filling
    /// it in, not a prerequisite.
    /// </summary>
    /// <remarks>
    /// APM is an expected dependency, never an installed one. Kyber-Weave does not put
    /// software on a machine as a side effect of scaffolding documentation, so this path
    /// tells the operator what to run and stops.
    /// </remarks>
    private static void SkillUnavailable(string command, string reason)
    {
        AnsiConsole.MarkupLine($"  [yellow]skipped[/]  kyber-weave-docs skill — {Markup.Escape(reason)}.");
        AnsiConsole.MarkupLine("           APM is an expected dependency; Kyber-Weave does not install it for you.");
        AnsiConsole.MarkupLine($"           Install it ([grey]https://microsoft.github.io/apm[/]), then run:");
        AnsiConsole.MarkupLine($"             [grey]{Markup.Escape(command)}[/]");
        AnsiConsole.MarkupLine("           Or re-run with [grey]--no-skill[/] to skip this step.");
        AnsiConsole.WriteLine();
    }
}
