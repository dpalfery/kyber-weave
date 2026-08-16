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
        (int ExitCode, ScaffoldResult? Result, string? Error) attempt = TryScaffold(settings);
        if (attempt.ExitCode != 0)
        {
            AnsiConsole.MarkupLine($"[red]Could not scaffold: {Markup.Escape(attempt.Error!)}[/]");
            return attempt.ExitCode;
        }

        ScaffoldResult result = attempt.Result!;
        string rootNote = result.DocsRootSource switch
        {
            DocsRootSource.Configuration => " [grey](configured)[/]",
            DocsRootSource.Convention => " [grey](detected)[/]",
            DocsRootSource.Explicit => string.Empty,
            _ => throw new InvalidOperationException(
                $"Unknown documentation root source: {result.DocsRootSource}")
        };
        AnsiConsole.MarkupLine($"Documentation root: [bold]{Markup.Escape(result.DocsRoot)}[/]{rootNote}");
        AnsiConsole.WriteLine();

        foreach (ScaffoldedFile file in result.Files)
        {
            AnsiConsole.MarkupLine($"  {Label(file.Outcome)} {Markup.Escape(file.RelativePath)}{Note(file)}");
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
        AnsiConsole.MarkupLine($"  4. Declare your stacks in [grey]ontology.technologies[/] and re-run, to scaffold a coding standard per technology under [grey]{Markup.Escape(result.DocsRoot)}/standards/[/].");

        return 0;
    }

    /// <summary>Runs the scaffold step and translates expected operator failures to exit 1.</summary>
    internal static (int ExitCode, ScaffoldResult? Result, string? Error) TryScaffold(
        DocsInitSettings settings)
    {
        try
        {
            ScaffoldResult result = DocsScaffolder.Scaffold(
                settings.Path, settings.DocsRoot, settings.Owner, settings.Force);
            return (0, result, null);
        }
        catch (Exception ex) when (ex is
            IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            // InvalidDataException carries KW-CONFIG-001 for a broken host config;
            // ArgumentException covers an escaping docs root or structurally unsafe owner.
            return (1, null, ex.Message);
        }
    }

    /// <summary>Fixed-width so the file paths line up under one another.</summary>
    private static string Label(ScaffoldOutcome outcome) => outcome switch
    {
        ScaffoldOutcome.Created => "[green]created[/]  ",
        ScaffoldOutcome.Updated => "[green]updated[/]  ",
        ScaffoldOutcome.Preserved => "[grey]kept[/]     ",
        _ => "[grey]exists[/]   "
    };

    private static string Note(ScaffoldedFile file)
    {
        string? note = file.Note ?? (file.Outcome == ScaffoldOutcome.Skipped
            ? "left alone; --force to overwrite"
            : null);

        return note is null ? string.Empty : $" [grey]({Markup.Escape(note)})[/]";
    }

    private static void DeploySkill(DocsInitSettings settings)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo("apm")
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

        string command = $"apm install {SkillPackage} --target {settings.Target}";

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                SkillUnavailable(command, "the 'apm' process could not be started");
                return;
            }

            ProcessResult result = ProcessRunner.ReadToEnd(process);

            if (result.ExitCode == 0)
            {
                AnsiConsole.MarkupLine($"  [green]deployed[/] kyber-weave-docs skill → target [bold]{Markup.Escape(settings.Target)}[/]");
                AnsiConsole.WriteLine();
                return;
            }

            string error = result.StandardError.Trim();
            string detail = error.Length > 0 ? error : result.StandardOutput.Trim();
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
