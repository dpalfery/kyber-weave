using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Mcp;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Docs;

/// <summary>
/// Renames the MCP server keys existing installs were written under to the names this version
/// ships, in place, and refuses every entry it cannot prove is Kyber-Weave's.
/// </summary>
/// <remarks>
/// <para>
/// This is the migration half of the rename, and it is a separate verb from <c>docs mcp-wire</c> on
/// purpose: wiring adds what a declaration asks for, while migrating changes the name an existing
/// entry already has. Doing the second by adding the new name and removing the old one is what this
/// verb exists to avoid — that leaves a window in which one file holds one server under two names,
/// and it loses the entry's other properties.
/// </para>
/// <para>
/// With no <c>--target</c> it is a preview that writes nothing at all, not even the receipt. A named
/// target is the consent to write that file, exactly as it is for the wire verb.
/// </para>
/// <para>
/// A user-scope file — under the operator's home directory — is never read or written. The report
/// names those files so the operator can rename a key there by hand.
/// </para>
/// <para>
/// The catalog holds <em>repository-relative</em> paths, so every bound in this verb is containment
/// under the root the operator named. Point that root at a home directory and the same relative path
/// — <c>.cursor/mcp.json</c> — is the user-scope file ADR 0052 decision 2 says is never read or
/// written. Containment alone is therefore not the guarantee this paragraph states, and a root that
/// <em>is</em> the operator's home directory is refused before the first read, with no byte written
/// and no receipt. The home is resolved through an injected reader — as
/// <c>RepositoryDoctor.Run</c> does, defaulting to the process environment — so the refusal is
/// testable without touching the developer's own home.
/// </para>
/// </remarks>
public sealed class DocsMcpMigrateCommand : Command<DocsMcpMigrateSettings>
{
    /// <summary>The removable, writable targets, spelled once for the usage message.</summary>
    private static string TargetList =>
        string.Join(
            ", ",
            HarnessMcpCatalog.Rows
                .Where(row => row.Fixability == McpWiringFixability.Fixable && row.Shape == McpConfigurationShape.Json)
                .Select(row => row.Harness)
                .Append(McpServerRenamer.DeclarationTarget));

    private readonly Func<string?> _home;

    /// <summary>
    /// Creates the command Spectre resolves: the operator's home is the process user profile.
    /// </summary>
    public DocsMcpMigrateCommand()
        : this(null)
    {
    }

    /// <summary>
    /// Creates the command with an injectable home reader, so the home-directory refusal can be
    /// exercised against a fixture rather than the developer's own profile.
    /// </summary>
    /// <param name="home">
    /// Resolves the operator's home directory, or null for
    /// <see cref="Environment.SpecialFolder.UserProfile"/> under the process environment.
    /// </param>
    public DocsMcpMigrateCommand(Func<string?>? home) =>
        _home = home ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <inheritdoc />
    public override int Execute(
        CommandContext context,
        DocsMcpMigrateSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        DiagnosticReport report = new();
        if (!DocsMcpWireCommand.TryRoot(settings.Path, report, out string root))
        {
            DocsMcpWireCommand.PrintDiagnostics(report);
            return 1;
        }

        // Before the first read: a root that is the operator's home directory would make every
        // catalog path a user-scope file, which is the thing this verb promises never to touch.
        if (IsOperatorHome(root, _home()))
        {
            CommandHelpers.WriteUnfolded(() => PrintHomeRefusal(root));
            return 1;
        }

        IReadOnlyList<string> targets = DocsMcpWireCommand.Targets(settings.Target);
        bool preview = targets.Count == 0;
        IReadOnlyList<McpRenameChange> changes = preview
            ? McpServerRenamer.Scan(root)
            : McpServerRenamer.Migrate(root, targets, settings.DryRun);

        CommandHelpers.WriteUnfolded(() =>
        {
            PrintHeader(root, preview, settings.DryRun);

            foreach (McpRenameChange change in changes)
                PrintChange(change, settings.DryRun);

            if (changes.Count == 0)
                AnsiConsole.MarkupLine("  [green]nothing to do[/] no retired MCP server key was found");

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(preview
                ? $"[grey]Nothing was written. Name the targets to migrate: {Markup.Escape(McpServerRenamer.MigrateCommand)} "
                    + $"<{Markup.Escape(TargetList)}>. Add --dry-run to see what a write would change.[/]"
                : "[grey]A user-scope file is still yours to edit: Kyber-Weave reads and writes repository-scope files only.[/]");
        });

        return preview || !changes.Any(change => change.Outcome == McpRenameOutcome.Refused) ? 0 : 1;
    }

    /// <summary>The two-argument overload the rest of this branch's commands carry.</summary>
    public int Execute(CommandContext context, DocsMcpMigrateSettings settings) =>
        Execute(context, settings, CancellationToken.None);

    /// <summary>
    /// Prints the rename table and the user-scope pointer every run carries, so an operator reading a
    /// preview learns both what the old keys are and which files this command will never touch.
    /// </summary>
    private static void PrintHeader(string root, bool preview, bool dryRun)
    {
        string run = preview ? "no --target named, so nothing will be written" : dryRun ? "dry run, so nothing will be written" : "writing the named targets";

        AnsiConsole.MarkupLine($"[bold]MCP server key migration[/] for {Markup.Escape(root)}: {Markup.Escape(run)}.");
        AnsiConsole.WriteLine();

        foreach (McpRenameRule rule in McpServerRenamer.Rules)
        {
            string launch = rule.NextArgument is null ? rule.Binary : $"{rule.Binary} {rule.NextArgument}";
            AnsiConsole.MarkupLine(
                $"  [bold]{Markup.Escape(rule.OldKey)}[/] [grey]→[/] [bold]{Markup.Escape(rule.NewKey)}[/] "
                + $"[grey](a launch of {Markup.Escape(launch)} proves the entry ours)[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            "[grey]User-scope files are never read or written: ~/.claude.json, ~/.cursor/mcp.json, ~/.codex/config.toml, "
            + "~/.config/opencode/opencode.json, ~/.factory/mcp.json, ~/.copilot/mcp-config.json, and Kilo's and ZCode's "
            + "user tiers. Rename a key there by hand.[/]");
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Whether <paramref name="root"/> is the operator's home directory, compared as a canonical path.
    /// </summary>
    /// <remarks>
    /// The comparison is on the resolved directory, never on the spelling: a home reached through a
    /// symbolic link, a relative path, or a <c>..</c> that lands there is the same directory, and a
    /// refusal that let one spelling through would be a refusal an operator could walk around by
    /// accident. A home this process cannot resolve, or does not have, cannot equal anything.
    /// </remarks>
    private static bool IsOperatorHome(string root, string? home)
    {
        if (string.IsNullOrWhiteSpace(home))
            return false;

        string? canonicalRoot = Canonical(root);
        string? canonicalHome = Canonical(home);

        return canonicalRoot is not null
            && canonicalHome is not null
            && string.Equals(
                canonicalRoot,
                canonicalHome,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>The directory a path names, with its own link resolved and its trailing separator removed.</summary>
    private static string? Canonical(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);

            if (Directory.Exists(full) && Directory.ResolveLinkTarget(full, returnFinalTarget: true) is { } target)
                full = Path.GetFullPath(target.FullName);

            string trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // A filesystem root trims to nothing, and "" is not a directory name this can compare.
            return trimmed.Length == 0 ? full : trimmed;
        }
        catch (Exception exception) when (exception is
            ArgumentException or IOException or NotSupportedException
            or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// The whole report for a root that is the home directory: one refusal naming the reason, and no
    /// table, because there is nothing this run may look at.
    /// </summary>
    private static void PrintHomeRefusal(string root)
    {
        AnsiConsole.MarkupLine(
            $"  [red]{Markup.Escape(McpServerRenamer.RefusedCode)}[/] root "
            + $"[grey]({Markup.Escape(TerminalText.Line(root))})[/]: the root is the operator's home directory, "
            + "whose files this command never reads or writes, so nothing was read and nothing was written");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            "    [grey]→ Name the repository that holds your MCP configuration: every path in this command's "
            + "catalog is repository-relative, so a home directory here means user scope. Edit a user-scope "
            + "file by hand.[/]");
    }

    /// <summary>Reports one change — or one refusal — as the operator reads it.</summary>
    /// <remarks>
    /// Every value that came out of a file goes through <see cref="TerminalText"/> before the terminal
    /// sees it, and the markup escaping is a second, separate concern: one stops a bracket being read
    /// as a tag, the other stops a control character moving the operator's cursor.
    /// </remarks>
    private static void PrintChange(McpRenameChange change, bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(change);

        string target = change.Path is null
            ? Markup.Escape(TerminalText.Line(change.Harness))
            : $"{Markup.Escape(TerminalText.Line(change.Harness))} [grey]({Markup.Escape(TerminalText.Line(change.Path))})[/]";

        if (change.Outcome == McpRenameOutcome.NothingToDo)
        {
            AnsiConsole.MarkupLine($"  [green]nothing to do[/] {target}: no retired key was found");
            return;
        }

        string code = Markup.Escape(change.Code ?? McpServerRenamer.RenameCode);

        if (change.Outcome == McpRenameOutcome.Refused)
        {
            AnsiConsole.MarkupLine($"  [red]{code}[/] {target}");
            PrintDetail(change.Message);
            PrintHint(change.Hint);
            PrintSnippet(change.Snippet);
            return;
        }

        string style = change.Outcome == McpRenameOutcome.Reported ? "grey" : "yellow";
        string note = dryRun && change.Outcome != McpRenameOutcome.Reported
            ? " [grey](dry run; nothing written)[/]"
            : string.Empty;

        AnsiConsole.MarkupLine($"  [{style}]{code}[/] {target}: {Markup.Escape(TerminalText.Line(change.Message))}{note}");
        PrintHint(change.Hint);
        PrintSnippet(change.Snippet);
    }

    private static void PrintDetail(string? message)
    {
        if (!string.IsNullOrEmpty(message))
            AnsiConsole.MarkupLine($"    {Markup.Escape(TerminalText.Line(message))}");
    }

    private static void PrintHint(string? hint)
    {
        if (!string.IsNullOrEmpty(hint))
            AnsiConsole.MarkupLine($"    [grey]→ {Markup.Escape(TerminalText.Line(hint))}[/]");
    }

    private static void PrintSnippet(string? snippet)
    {
        if (string.IsNullOrEmpty(snippet))
            return;

        AnsiConsole.MarkupLine("    [grey]Paste this into that file instead:[/]");

        // Written raw: a snippet is TOML or JSON, and Spectre markup would read its brackets as tags
        // and drop them. The control characters go before the brackets are allowed through.
        AnsiConsole.WriteLine(TerminalText.Block(snippet));
    }
}
