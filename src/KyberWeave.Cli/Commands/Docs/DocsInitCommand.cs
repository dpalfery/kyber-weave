using System.Diagnostics;
using KyberWeave.Cli.Commands.Doctor;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Doctor;
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

    /// <summary>
    ///     The bound for the APM install. It fetches the package over the network, so it gets minutes; a hung
    ///     fetch is stopped and the skill reported as not deployed.
    /// </summary>
    private static readonly TimeSpan ApmInstallTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     The bound for a provisioning run's CodeGraph child. It matches the doctor verb's default, so
    ///     <c>docs init --provision</c> behaves like a bare <c>kyber-weave doctor --provision</c>.
    /// </summary>
    private static readonly TimeSpan ProvisionIndexTimeout = TimeSpan.FromSeconds(600);

    /// <summary>Runs the provisioning doctor's CodeGraph child. Null uses a process executor bounded by <see cref="ProvisionIndexTimeout"/>.</summary>
    internal IProcessExecutor? Executor { get; init; }

    /// <summary>The MCP executable the provisioning doctor proves. Null uses the PATH name <c>kyber-weave-mcp</c>.</summary>
    internal string? McpExecutable { get; init; }

    /// <inheritdoc />
    public override int Execute(CommandContext context, DocsInitSettings settings, CancellationToken cancellationToken)
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

        if (result.ReviewGates is not null)
        {
            PrintReviewGates(result.ReviewGates);
        }

        AnsiConsole.WriteLine();

        if (!settings.NoSkill)
        {
            DeploySkill(settings);
        }

        ReportRetiredSkills(settings);

        AnsiConsole.MarkupLine("[bold]Next:[/]");
        AnsiConsole.MarkupLine($"  1. Replace the example row in [grey]{Markup.Escape(result.DocsRoot)}/catalog.md[/] with your real components and owners.");
        AnsiConsole.MarkupLine($"  2. Run [grey]kyber-weave docs validate {Markup.Escape(settings.Path)}[/] to see what the corpus needs.");
        AnsiConsole.MarkupLine("  3. Ask your agent to apply the [grey]kyber-docs-authoring[/] skill to the failing documents.");
        AnsiConsole.MarkupLine($"  4. Declare your stacks in [grey]ontology.technologies[/] and re-run, to scaffold a coding standard per technology under [grey]{Markup.Escape(result.DocsRoot)}/standards/[/].");

        return settings.Provision ? Provision(settings) : 0;
    }

    /// <summary>
    ///     Runs the repository doctor in provision mode against the scaffolded repository. The root is the git
    ///     toplevel of the target path, which the doctor resolves itself.
    /// </summary>
    /// <remarks>
    ///     <c>--target</c> names the APM harnesses that receive the skill, not a path, so provisioning always goes to
    ///     the repository that holds the scaffold. Pure scaffolding stays the default, so CI's scaffold step is
    ///     unchanged unless it opts in.
    /// </remarks>
    private int Provision(DocsInitSettings settings)
    {
        DoctorOptions options = new(
            ProvisionIndexTimeout,
            TimeSpan.FromSeconds(20),
            "codegraph",
            McpExecutable ?? "kyber-weave-mcp");
        IProcessExecutor executor = Executor ?? ProcessExecutor.WithTimeout(ProvisionIndexTimeout);

        DoctorReport report = RepositoryDoctor.Run(
            Path.GetFullPath(settings.Path),
            DoctorMode.Provision,
            options,
            executor,
            Environment.GetEnvironmentVariable);

        DoctorReportRenderer.Write(report, asJson: false);
        return report.ExitCode;
    }

    /// <summary>Runs the scaffold step and translates expected operator failures to exit 1.</summary>
    internal static (int ExitCode, ScaffoldResult? Result, string? Error) TryScaffold(
        DocsInitSettings settings)
    {
        try
        {
            ScaffoldResult result = DocsScaffolder.Scaffold(
                settings.Path,
                settings.DocsRoot,
                settings.Owner,
                settings.Force,
                settings.KyberStandards,
                reviewGates: !settings.NoReviewGates);
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

    /// <summary>
    /// Reports what the scaffold did about the catalogue review gates: the ids written per
    /// technology, the gates written commented out with their reasons, the technologies with
    /// no gates to ship, and — when the host owns the section — the catalogue ids missing
    /// from it. A missing section is information only; the exit code is unchanged.
    /// </summary>
    /// <remarks>
    /// Printed unfolded like the retired-skill cleanup command below: a redirected console
    /// folds at 80 columns, and a wrapped gate id or reason would fail an exact-match check
    /// as well as read badly.
    /// </remarks>
    private static void PrintReviewGates(ReviewGatesOutcome review)
    {
        WriteUnfolded(() => PrintReviewGatesUnfolded(review));
    }

    private static void PrintReviewGatesUnfolded(ReviewGatesOutcome review)
    {
        if (review.OptedOut)
        {
            AnsiConsole.MarkupLine("review gates not written (--no-review-gates)");
            return;
        }

        foreach (IGrouping<string, (string Technology, string GateId)> group in review.WrittenGates.GroupBy(
                     entry => entry.Technology, StringComparer.Ordinal))
        {
            string ids = string.Join(", ", group.Select(entry => entry.GateId));
            AnsiConsole.MarkupLine($"review gates for {Markup.Escape(group.Key)}: {Markup.Escape(ids)}");
        }

        foreach ((string gateId, string reason) in review.CommentedOutGates)
        {
            AnsiConsole.MarkupLine($"review gate '{Markup.Escape(gateId)}' commented out: {Markup.Escape(reason)}");
        }

        foreach (string technology in review.NoFamily)
        {
            AnsiConsole.MarkupLine($"no gates shipped for {Markup.Escape(technology)}");
        }

        foreach (string missing in review.MissingFromExisting)
        {
            AnsiConsole.MarkupLine(
                $"review gate '{Markup.Escape(missing)}' is in the catalogue but missing from the host configuration.");
        }

        if (review.Written)
        {
            AnsiConsole.MarkupLine(
                "CI: copy templates/github-actions/kyber-weave-review-gate.yml from https://github.com/dpalfery/kyber-weave into .github/workflows/.");
        }
    }

    private static void DeploySkill(DocsInitSettings settings)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo("apm")
        {
            WorkingDirectory = Path.GetFullPath(settings.Path),
            // Input is redirected only so the runner can close it at once: an APM prompt then reads end-of-file
            // rather than waiting on a terminal nobody can see, because its output is captured too.
            RedirectStandardInput = true,
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
            ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, ApmInstallTimeout);

            if (result.ExitCode == 0)
            {
                AnsiConsole.MarkupLine($"  [green]deployed[/] kyber-docs-authoring skill → target [bold]{Markup.Escape(settings.Target)}[/]");
                AnsiConsole.WriteLine();
                return;
            }

            string error = result.StandardError.Trim();
            string detail = error.Length > 0 ? error : result.StandardOutput.Trim();
            SkillUnavailable(command, $"apm exited {result.ExitCode}: {detail}");
        }
        catch (TimeoutException)
        {
            SkillUnavailable(command, "apm did not finish and was stopped after the time limit");
        }
        catch (InvalidOperationException)
        {
            // The runner could not start the process, which the skill reports like any other unavailable APM.
            SkillUnavailable(command, "the 'apm' process could not be started");
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
        AnsiConsole.MarkupLine($"  [yellow]skipped[/]  kyber-docs-authoring skill — {Markup.Escape(reason)}.");
        AnsiConsole.MarkupLine("           APM is an expected dependency; Kyber-Weave does not install it for you.");
        AnsiConsole.MarkupLine("           Install it ([grey]https://microsoft.github.io/apm[/]), then run:");
        AnsiConsole.MarkupLine($"             [grey]{Markup.Escape(command)}[/]");
        AnsiConsole.MarkupLine("           Or re-run with [grey]--no-skill[/] to skip this step.");
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Warns about retired Kyber-Docs skill directories the APM install leaves behind.
    /// </summary>
    /// <remarks>
    /// APM prunes a retired skill only when a lockfile records the install. That
    /// lockfile-gated pruning was observed on both 0.28.0 and 0.35.0: without a
    /// lockfile both versions leave the stale directory behind, so the warning fires
    /// on directory presence rather than on any one version's behaviour. No 0.34.0
    /// behaviour is pinned because none was observed. A warning rather than a
    /// failure: the stale copy still works, but it is no longer deployed.
    /// </remarks>
    private static void ReportRetiredSkills(DocsInitSettings settings)
    {
        string repoRoot = Path.GetFullPath(settings.Path);
        string[] targets = settings.Target.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] retired = ["kyber-weave-docs", "doc-corpus-integrity-review"];
        foreach (string target in targets)
        {
            string? skillsRoot = target.Trim().ToUpperInvariant() switch
            {
                "AGENT-SKILLS" => Path.Combine(repoRoot, ".agents", "skills"),
                "CLAUDE" => Path.Combine(repoRoot, ".claude", "skills"),
                _ => null,
            };
            if (skillsRoot is null)
            {
                continue;
            }

            foreach (string name in retired)
            {
                string staleDir = Path.Combine(skillsRoot, name);
                if (!Directory.Exists(staleDir))
                {
                    continue;
                }

                WriteUnfolded(() =>
                {
                    AnsiConsole.MarkupLine(
                        $"  [yellow]warn[/] Retired skill '{Markup.Escape(name)}' still present at [bold]{Markup.Escape(staleDir)}[/]. " +
                        "APM prunes retired skills only when a lockfile records the install (observed on 0.28.0 and 0.35.0); " +
                        "without a lockfile the stale directory is left behind.");
                    AnsiConsole.MarkupLine(
                        $"         Remove it with: [grey]rm -rf \"{Markup.Escape(staleDir)}\"[/]");
                });
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="write"/> with console folding lifted, then restores the width.
    /// </summary>
    /// <remarks>
    /// A redirected console folds at 80 columns, splitting a long path across lines and
    /// corrupting the copy-pastable cleanup command the warning exists for. The same
    /// unfolded-write pattern lives in <c>SquadCommandComposition</c> for the same reason;
    /// it is repeated here so the Docs branch does not depend on the Squad branch.
    /// </remarks>
    private static void WriteUnfolded(Action write)
    {
        int originalWidth = AnsiConsole.Profile.Width;
        AnsiConsole.Profile.Width = int.MaxValue;
        try
        {
            write();
        }
        finally
        {
            AnsiConsole.Profile.Width = originalWidth > 0 ? originalWidth : 80;
        }
    }
}
