using System.Diagnostics;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Mcp;
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
    /// Whether this run may ask the operator a question, defaulting to the terminal's own answer.
    /// </summary>
    /// <remarks>
    /// A seam rather than a parameter because the question it gates has to be unreachable in a test
    /// that does not want it asked, and a redirected stream cannot answer one. The default asks three
    /// things of the ambient console because a prompt needs all three: a stdin to read the answer
    /// from, a stdout that may receive interactive markup, and a console Spectre has not been told is
    /// non-interactive — which is how a capturing or piped console identifies itself, and attempting
    /// a prompt there throws rather than asking.
    /// </remarks>
    internal static Func<bool>? InteractiveCheckOverride { get; set; }

    /// <summary>
    /// Decides whether to perform the offered repair, defaulting to an
    /// <see cref="AnsiConsole"/> confirmation naming every file and server.
    /// </summary>
    /// <remarks>
    /// The answer is the consent, so it is taken through a seam the tests can answer with a fixed
    /// value: a case that exercised the real prompt would block on a terminal it does not have.
    /// </remarks>
    internal static Func<IReadOnlyList<McpWiringFix>, bool>? ConfirmFixOverride { get; set; }

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

        // The wiring check sits after the scaffold, the skill deploy and the retired-skill warnings,
        // and before the Next: block, because it is only meaningful once the declaration exists: a
        // fresh repository has just been handed its `mcp:` section, and that section is what the
        // check reads. Failing a scaffold therefore never reaches the question (R4.7).
        int wiringExitCode = ReportMcpWiring(settings);
        if (wiringExitCode != 0)
        {
            return wiringExitCode;
        }

        AnsiConsole.MarkupLine("[bold]Next:[/]");
        AnsiConsole.MarkupLine($"  1. Replace the example row in [grey]{Markup.Escape(result.DocsRoot)}/catalog.md[/] with your real components and owners.");
        AnsiConsole.MarkupLine($"  2. Run [grey]kyber-weave docs validate {Markup.Escape(settings.Path)}[/] to see what the corpus needs.");
        AnsiConsole.MarkupLine("  3. Ask your agent to apply the [grey]kyber-docs-authoring[/] skill to the failing documents.");
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
        CommandHelpers.WriteUnfolded(() => PrintReviewGatesUnfolded(review));
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

                CommandHelpers.WriteUnfolded(() =>
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
    /// Checks whether each declared MCP server reaches the harnesses this repository is in play for,
    /// prints the report, and offers the repair when a terminal can be asked.
    /// </summary>
    /// <returns>
    /// Zero whenever the scaffold succeeded, whatever the report found: an unwired harness is the
    /// normal state of a fresh adoption, not a failure of the command that scaffolded the corpus
    /// (R3.4). One only when the declaration itself cannot be loaded, which is the existing
    /// <c>KW-CONFIG-001</c> path rather than a wiring finding (R3.6).
    /// </returns>
    /// <remarks>
    /// The declaration is loaded from disk after the scaffold rather than taken from it, so the run
    /// that just wrote a repository's <c>mcp:</c> section checks that section instead of the empty
    /// one the repository had a moment earlier.
    /// </remarks>
    private static int ReportMcpWiring(DocsInitSettings settings)
    {
        if (settings.NoMcpCheck)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("MCP wiring not checked (--no-mcp-check).");
            return 0;
        }

        string root = Path.GetFullPath(settings.Path);
        KyberWeaveConfigLoadResult loaded = KyberWeaveConfigLoader.TryLoad(root);

        if (!loaded.Success)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                $"[red]{Markup.Escape(KyberWeaveConfigLoader.ConfigLoadErrorCode)}[/]: "
                + Markup.Escape(loaded.Error ?? "kyber-weave.yml could not be read."));
            return 1;
        }

        // A repository that declares no servers has nothing to check, and printing one
        // not-detectable line per harness for it would be pure noise (R1.4).
        if (loaded.Config is null || loaded.Config.Mcp.RequiredServers.Count == 0)
            return 0;

        McpConfig config = loaded.Config.Mcp;
        McpWiringReport report = McpWiringInspector.Inspect(root, config);

        AnsiConsole.WriteLine();
        PrintWiringSection(report, config);
        AnsiConsole.WriteLine();

        IReadOnlyList<McpWiringFix> fixes = McpWiringFixes.Derive(report, root);
        if (fixes.Count > 0 && CanPrompt())
            OfferRepair(root, config, fixes);

        return 0;
    }

    /// <summary>
    /// Prints the section unfolded: one <c>ok</c> line per harness whose declared servers are all
    /// wired and launchable, and a finding line carrying its rule id for every gap, with the remedy
    /// on its own line beneath.
    /// </summary>
    /// <remarks>
    /// Unfolded for the reason <see cref="ReportRetiredSkills"/> is: a redirected console folds at 80
    /// columns, and a finding whose rule id, server name and path are split across lines is neither
    /// readable nor copy-pasteable.
    /// </remarks>
    private static void PrintWiringSection(McpWiringReport report, McpConfig config)
    {
        RequiredMcpServer[] declared =
        [
            .. config.RequiredServers.Values.OrderBy(server => server.Name, StringComparer.Ordinal)
        ];

        CommandHelpers.WriteUnfolded(() =>
        {
            AnsiConsole.MarkupLine("[bold]MCP wiring:[/]");

            foreach (HarnessMcpEntry row in HarnessMcpCatalog.Rows)
            {
                McpWiringFinding? undetectable = report.Findings.FirstOrDefault(
                    finding => string.Equals(finding.Harness, row.Harness, StringComparison.Ordinal)
                        && finding.Kind == McpWiringKind.NotDetectable);

                if (undetectable is not null)
                {
                    PrintFinding(undetectable);
                    continue;
                }

                // A harness with no finding and no not-detectable line is in play and fully wired:
                // that is what the absence of both means, so the section can confirm it rather than
                // only ever complaining (R3.5).
                List<McpWiringFinding> gaps =
                [
                    .. declared
                        .Select(server => report.Findings.FirstOrDefault(
                            finding => string.Equals(finding.Harness, row.Harness, StringComparison.Ordinal)
                                && string.Equals(finding.Server, server.Name, StringComparison.Ordinal)
                                && finding.Kind != McpWiringKind.NotDetectable))
                        .OfType<McpWiringFinding>()
                ];

                if (gaps.Count == 0 && declared.Length > 0)
                {
                    AnsiConsole.MarkupLine(
                        $"  [green]ok[/]    {Markup.Escape(row.Harness)}: "
                        + $"{Markup.Escape(TerminalText.Line(string.Join(", ", declared.Select(server => server.Name))))} wired");
                    continue;
                }

                foreach (McpWiringFinding gap in gaps)
                    PrintFinding(gap);
            }
        });
    }

    /// <summary>Prints one finding with its rule id, then its remedy on the line beneath.</summary>
    /// <remarks>
    /// The message names the declared server and the hint quotes the declared and the wired command,
    /// so both are text somebody else wrote — <c>kyber-weave.yml</c> or a harness's own configuration
    /// — and both go through <see cref="TerminalText"/> before the terminal sees them.
    /// </remarks>
    private static void PrintFinding(McpWiringFinding finding)
    {
        string level = finding.Severity switch
        {
            Severity.Error or Severity.Critical => "[red]error[/]",
            Severity.Warning => "[yellow]warn[/] ",
            _ => "[grey]info[/] "
        };

        AnsiConsole.MarkupLine(
            $"  {level} {Markup.Escape(finding.Code)} {Markup.Escape(finding.Harness)}: "
            + Markup.Escape(TerminalText.Line(finding.Message)));

        // A not-detectable line's hint is a paragraph of advice about a harness nobody claimed to
        // use; every other finding's hint is the command that closes it, and that is printed
        // verbatim so it can be copied. The Copilot coding agent is the exception the rule has to
        // make room for: its hint *is* its report, because there is no file in the tree to read or
        // write (2.5), and suppressing it left the operator told to configure a harness with no
        // statement of how. The exception is decided by the catalog's shape — repository settings,
        // not a file — rather than by the harness's name.
        if (AdviceBelongsToOperator(finding) && !string.IsNullOrWhiteSpace(finding.Hint))
            AnsiConsole.MarkupLine($"        [grey]{Markup.Escape(TerminalText.Line(finding.Hint))}[/]");
    }

    /// <summary>
    /// Whether a finding's hint is the operator's to act on, rather than advice about a harness this
    /// repository may not use at all (2.4).
    /// </summary>
    private static bool AdviceBelongsToOperator(McpWiringFinding finding) =>
        finding.Kind != McpWiringKind.NotDetectable
        || (!string.IsNullOrWhiteSpace(finding.Harness)
            && HarnessMcpCatalog.Find(finding.Harness)?.Shape == McpConfigurationShape.RepositorySettings);

    /// <summary>Whether this run may ask the operator, honouring the seam when a test set it.</summary>
    private static bool CanPrompt() =>
        InteractiveCheckOverride?.Invoke()
            ?? (!Console.IsInputRedirected
                && !Console.IsOutputRedirected
                && AnsiConsole.Profile.Capabilities.Interactive);

    /// <summary>
    /// Asks whether to perform the repair, and performs it through the same bounded write path as the
    /// dedicated repair command when the answer is yes (R4.1–R4.3).
    /// </summary>
    /// <remarks>
    /// Declining is not declining the diagnosis: the findings and their manual command were printed
    /// before the question and stand as the operator's route to the same repair. Nothing is written
    /// on a decline, and nothing is written when the prompt could not be shown at all — the
    /// <see cref="CanPrompt"/> gate is the caller's, so a redirected run never reaches this method.
    /// </remarks>
    private static void OfferRepair(string root, McpConfig config, IReadOnlyList<McpWiringFix> fixes)
    {
        Func<IReadOnlyList<McpWiringFix>, bool> confirm = ConfirmFixOverride ?? ConfirmRepair;

        if (!confirm(fixes))
            return;

        WriteRepair(root, config, fixes);
    }

    /// <summary>
    /// The real confirmation: one question naming every file and every server the repair would write.
    /// </summary>
    /// <remarks>
    /// It defaults to no. The repair writes files a harness will load and a receipt into version
    /// control, and a bare Enter is not a considered yes for that.
    /// </remarks>
    private static bool ConfirmRepair(IReadOnlyList<McpWiringFix> fixes)
    {
        string targets = string.Join(
            "; ",
            fixes.Select(fix => $"{fix.Harness} ({fix.Path}) adding {string.Join(", ", fix.Servers)}"));

        return AnsiConsole.Confirm(
            Markup.Escape(TerminalText.Line($"Wire the declared MCP servers now? This writes {targets}.")),
            defaultValue: false);
    }

    /// <summary>
    /// Runs the repair over the harnesses the fixes name and reports what reached disk, per file, with
    /// the receipt's location (R4.2).
    /// </summary>
    /// <remarks>
    /// The same call the dedicated repair command makes, with the same resolver, so a confirmation
    /// here and an explicit run of that command cannot write different bytes. A refused target is
    /// reported with its own rule id and remedy and does not fail the run: the corpus was scaffolded,
    /// which is what this command exists to do.
    /// </remarks>
    private static void WriteRepair(string root, McpConfig config, IReadOnlyList<McpWiringFix> fixes)
    {
        McpWiringRepairReport repair = McpWiringRepairer.Repair(
            root,
            config,
            LaunchCommandResolver.FromEnvironment(),
            McpWiringFixes.Harnesses(fixes));

        CommandHelpers.WriteUnfolded(() =>
        {
            bool wrote = false;

            foreach (McpWiringFileChange change in repair.Files)
            {
                string path = change.Path ?? change.Harness;

                if (change.Written)
                {
                    wrote = true;
                    AnsiConsole.MarkupLine(
                        $"  [green]wrote[/]  {Markup.Escape(path)} ({Markup.Escape(TerminalText.Line(string.Join(", ", change.Servers)))})");

                    if (!string.IsNullOrWhiteSpace(change.Message))
                        AnsiConsole.MarkupLine($"         [grey]{Markup.Escape(TerminalText.Line(change.Message))}[/]");

                    continue;
                }

                AnsiConsole.MarkupLine(
                    $"  [yellow]refused[/] {Markup.Escape(change.Code ?? McpWiringRepairer.RefusalCode)} "
                    + Markup.Escape(TerminalText.Line(change.Message ?? $"{path} was not written.")));

                if (!string.IsNullOrWhiteSpace(change.Hint))
                    AnsiConsole.MarkupLine($"          [grey]{Markup.Escape(TerminalText.Line(change.Hint))}[/]");
            }

            if (wrote)
            {
                AnsiConsole.MarkupLine(
                    $"  Receipt: [grey]{Markup.Escape(McpWiringReceipt.RelativePath)}[/]");
            }

            AnsiConsole.WriteLine();
        });
    }

}
