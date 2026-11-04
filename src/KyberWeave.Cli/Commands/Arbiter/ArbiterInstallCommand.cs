using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Cli.Update;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>
/// One-command Arbiter install: ensures the host config, sets
/// <c>arbiter.enabled: true</c> non-destructively, renders the target through the
/// existing Squad install pipeline, applies the provider choice, stores the key in
/// the OS credential store, verifies, and prints the target's trust step.
/// </summary>
/// <remarks>
/// A second identical run is an idempotent no-op with a status line: no config
/// rewrite, no duplicate credential-store write, no re-render. The key is never
/// printed and never written to configuration, receipts, or deployed files; only
/// its presence in the store is reported. <c>--arbiter-key</c> is accepted with a
/// one-line argv/shell-history warning, while <c>--key-stdin</c> and
/// <c>TYPESAFE_API_KEY</c> remain the safer paths.
/// </remarks>
public sealed class ArbiterInstallCommand : Command<ArbiterInstallSettings>
{
    private readonly ISquadUserPaths? _userPaths;
    private readonly SquadStateStore? _stateStore;
    private readonly ISquadReleaseSource? _releaseSource;
    private readonly ISquadRenderer? _renderer;
    private readonly bool? _isInteractive;
    private readonly Func<string, bool>? _readAnswer;
    private readonly Func<string, string?>? _environmentReader;

    /// <summary>Creates a new install command using default dependencies.</summary>
    public ArbiterInstallCommand()
    {
    }

    /// <summary>Creates a new install command over injected collaborators.</summary>
    internal ArbiterInstallCommand(
        ISquadUserPaths? userPaths = null,
        SquadStateStore? stateStore = null,
        ISquadReleaseSource? releaseSource = null,
        ISquadRenderer? renderer = null,
        bool? isInteractive = null,
        Func<string, bool>? readAnswer = null,
        Func<string, string?>? environmentReader = null)
    {
        _userPaths = userPaths;
        _stateStore = stateStore;
        _releaseSource = releaseSource;
        _renderer = renderer;
        _isInteractive = isInteractive;
        _readAnswer = readAnswer;
        _environmentReader = environmentReader;
    }

    /// <inheritdoc />
    public override int Execute(CommandContext context, ArbiterInstallSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.Global)
        {
            WriteError(
                "--global is not supported for 'arbiter install': " +
                "the install is project-scoped and never writes to the home tree.");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(settings.Target))
        {
            WriteError($"--target is required. Known targets: {Roster()}, all.");
            return 2;
        }

        string targetToken = settings.Target.Trim();
        if (!SquadTargetCatalog.TryResolve(targetToken, out SquadTarget target))
        {
            WriteError($"Unknown target '{targetToken}'. Known targets: {Roster()}, all.");
            return 2;
        }

        if (!TryParseProvider(settings.Provider, out ArbiterProviderKind providerKind))
        {
            WriteError($"--provider '{settings.Provider}' is not supported. Use none or systemone.");
            return 2;
        }

        string kindName = providerKind == ArbiterProviderKind.Systemone ? "systemone" : "none";
        string root = Path.GetFullPath(string.IsNullOrWhiteSpace(settings.Path) ? "." : settings.Path);

        if (!string.IsNullOrWhiteSpace(settings.ArbiterKey))
        {
            AnsiConsole.MarkupLine(
                "[yellow]warn:[/] --arbiter-key is visible in process argv and shell history; " +
                $"prefer --key-stdin or {Markup.Escape(ArbiterKeyResolver.EnvVarName)}.");
        }

        string endpoint = ResolveEndpoint(root, settings.Config);
        string origin = ArbiterKeyResolver.GetOrigin(endpoint) ?? ArbiterKeyResolver.TypeSafeOrigin;
        bool needsKey = providerKind == ArbiterProviderKind.Systemone
            && !IsLoopbackEndpoint(endpoint);

        string? keyToStore = NullIfEmpty(settings.ArbiterKey);
        if (needsKey && keyToStore is null && settings.KeyStdin)
            keyToStore = NullIfEmpty(ReadStdinKey());

        if (needsKey && keyToStore is null)
        {
            if (!string.IsNullOrEmpty(ResolveStoredKey(endpoint)))
            {
                keyToStore = null;
            }
            else
            {
                string? prompted = settings.KeyStdin ? null : NullIfEmpty(ReadPromptedKey());
                if (!string.IsNullOrEmpty(prompted))
                {
                    keyToStore = prompted;
                }
                else
                {
                    WriteError(
                        $"{ArbiterStatusCommand.KeyMissing}: A remote provider is configured, " +
                        $"and no key resolves for origin '{origin}'. " +
                        "Run with --arbiter-key, --key-stdin, or export " +
                        $"{ArbiterKeyResolver.EnvVarName}.");
                    return 2;
                }
            }
        }

        string? existingStored = needsKey ? ReadStoredKey(origin) : null;
        bool keyWriteNeeded = needsKey
            && !string.IsNullOrEmpty(keyToStore)
            && !string.Equals(existingStored, keyToStore, StringComparison.Ordinal);

        string configPath = ResolveConfigPath(root, settings.Config);
        string? existingText = ReadConfigText(configPath);
        if (existingText is null)
            return 1;

        string desiredText = EnsureProviderKind(EnsureArbiterEnabled(existingText), kindName);
        bool configChanged = !string.Equals(desiredText, existingText, StringComparison.Ordinal);

        SquadStateStore stateStore =
            _stateStore ?? SquadCommandComposition.ResolveStateStore(_userPaths, _environmentReader);

        SquadReceipt? existingReceipt;
        try
        {
            existingReceipt = stateStore.ReadReceipt(root, SquadDeploymentScope.Project);
        }
        catch (Exception ex) when (ex is InvalidDataException
            or IOException
            or UnauthorizedAccessException)
        {
            WriteError($"The Squad deployment state for '{root}' could not be read: {ex.Message}");
            return 1;
        }

        bool receiptMatches = existingReceipt is not null && ReceiptTargetsMatch(existingReceipt, targetToken);

        if (!configChanged && !keyWriteNeeded && receiptMatches)
        {
            AnsiConsole.MarkupLine(
                $"[grey]Arbiter already installed for target '[bold]{Markup.Escape(targetToken)}[/]' " +
                $"in '[bold]{Markup.Escape(root)}[/]'. Nothing to do.[/]");
            return 0;
        }

        if (!settings.DryRun && !SquadTargetRootConfirmation.Confirm(
                root,
                SquadDeploymentScope.Project,
                "install",
                _isInteractive ?? SquadCommandComposition.IsInteractiveConsole(),
                settings.Yes,
                _readAnswer ?? SquadTargetRootConfirmation.ReadConsoleAnswer))
        {
            AnsiConsole.MarkupLine("[yellow]Declined. No changes were made.[/]");
            return 2;
        }

        if (!settings.DryRun)
        {
            if (configChanged && !WriteConfigText(configPath, desiredText))
                return 1;

            if (keyWriteNeeded && keyToStore is not null && !WriteStoredKey(origin, keyToStore))
                return 1;
        }

        KyberWeaveConfigLoadResult configLoad = KyberWeaveConfigLoader.TryLoad(root, settings.Config);
        if (!configLoad.Success || configLoad.Config is null)
        {
            WriteError(
                $"The host configuration '{configLoad.ConfigPath ?? configPath}' could not be loaded: " +
                $"{configLoad.Error ?? "unknown load failure"}.");
            return 1;
        }

        SquadArbiterWiring wiring =
            SquadCommandComposition.ResolveArbiterWiring(configLoad.Config.Arbiter);

        SquadLifecycleService lifecycle = CreateLifecycleService(stateStore);
        SquadInstallRequest request = new(
            root,
            SquadDeploymentScope.Project,
            [target],
            DryRun: settings.DryRun,
            Arbiter: wiring);

        SquadLifecycleResult result;
        try
        {
            result = lifecycle.InstallAsync(request, cancellationToken).GetAwaiter().GetResult();
        }
        catch (SquadDeploymentConflictException ex)
        {
            WriteError(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            WriteError(ex.Message);
            return 1;
        }

        if (!result.Success)
        {
            if (result.Errors is { Count: > 0 })
            {
                foreach (string error in result.Errors)
                    WriteError(error);
            }
            else
            {
                WriteError("The Squad render failed.");
            }

            return 1;
        }

        if (settings.DryRun)
        {
            int fileCount = result.Receipt?.Files.Count ?? 0;
            SquadCommandComposition.WriteLine(
                $"[bold]Dry-run:[/] planned {fileCount} deployed files for [bold]{Markup.Escape(root)}[/].");
        }
        else
        {
            SquadCommandComposition.WriteLine(
                $"[green]Successfully installed Arbiter for target '[bold]{Markup.Escape(targetToken)}[/]' " +
                $"in '[bold]{Markup.Escape(root)}[/]'.[/]");
        }

        SquadInstallCommand.WriteArbiterTrustSteps([target], wiring);
        SquadCommandComposition.WriteTransactionWarnings(result.Warnings);

        if (!settings.DryRun && !Verify(root, settings.Config, endpoint, needsKey))
            return 1;

        return 0;
    }

    /// <summary>Runs the command without cancellation; the entry point tests use this.</summary>
    public int Execute(CommandContext context, ArbiterInstallSettings settings) =>
        Execute(context, settings, CancellationToken.None);

    private static bool TryParseProvider(string? raw, out ArbiterProviderKind kind)
    {
        kind = ArbiterProviderKind.None;
        if (string.IsNullOrWhiteSpace(raw))
            return true;

        string trimmed = raw.Trim();
        if (string.Equals(trimmed, "none", StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(trimmed, "systemone", StringComparison.OrdinalIgnoreCase))
        {
            kind = ArbiterProviderKind.Systemone;
            return true;
        }

        return false;
    }

    private static string Roster() =>
        string.Join(", ", SquadTargetCatalog.All.Select(SquadTargetCatalog.GetToken));

    private static void WriteError(string message)
    {
        CommandHelpers.WriteUnfolded(() =>
            AnsiConsole.MarkupLine($"[red]kyber-weave arbiter: error: {Markup.Escape(message)}[/]"));
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;

    private static string? ReadStdinKey()
    {
        try
        {
            return ArbiterCommandComposition.KeyStdinReader();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadPromptedKey()
    {
        try
        {
            return ArbiterCommandComposition.KeyPrompt();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private Func<string, string?> EnvironmentReader() => _environmentReader ?? Environment.GetEnvironmentVariable;

    private string ResolveEndpoint(string root, string? configOption)
    {
        KyberWeaveConfigLoadResult loaded = KyberWeaveConfigLoader.TryLoad(root, configOption);
        if (loaded.Success
            && loaded.Config is not null
            && !string.IsNullOrWhiteSpace(loaded.Config.Arbiter.Provider.Endpoint))
            return loaded.Config.Arbiter.Provider.Endpoint.Trim();

        return ArbiterSetupCommand.TypeSafeEndpoint;
    }

    private static bool IsLoopbackEndpoint(string endpoint)
    {
        try
        {
            return ArbiterKeyResolver.IsLoopbackEndpoint(endpoint);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private string? ResolveStoredKey(string endpoint)
    {
        ICredentialStore store;
        try
        {
            store = ArbiterCommandComposition.CredentialStore();
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            return null;
        }

        try
        {
            return ArbiterKeyResolver.Resolve(endpoint, store, EnvironmentReader());
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception
            or ArgumentException)
        {
            return null;
        }
    }

    private string? ReadStoredKey(string origin)
    {
        try
        {
            return ArbiterCommandComposition.CredentialStore().Read(origin);
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception
            or ArgumentException)
        {
            return null;
        }
    }

    private static bool WriteStoredKey(string origin, string key)
    {
        try
        {
            ArbiterCommandComposition.CredentialStore().Write(origin, key);
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception
            or ArgumentException)
        {
            WriteError(
                $"The credential store for origin '{origin}' is not available: {ex.Message} " +
                $"Store the key in {ArbiterKeyResolver.EnvVarName} instead.");
            return false;
        }

        AnsiConsole.MarkupLine($"[green]ok[/] Stored a key for {Markup.Escape(origin)}.");
        return true;
    }

    private static bool ReceiptTargetsMatch(SquadReceipt receipt, string targetToken)
    {
        HashSet<string> existing = receipt.Files
            .Select(file => file.Target)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        return existing.SetEquals([targetToken]);
    }

    private static string ResolveConfigPath(string root, string? configOption)
    {
        if (!string.IsNullOrWhiteSpace(configOption))
        {
            return Path.IsPathRooted(configOption)
                ? configOption
                : Path.Combine(root, configOption);
        }

        return FindConfigPath(root) ?? Path.Combine(root, ".kyber-weave", "kyber-weave.yml");
    }

    private static string? FindConfigPath(string root)
    {
        string defaultPath = Path.Combine(root, ".kyber-weave", "kyber-weave.yml");
        if (File.Exists(defaultPath))
            return defaultPath;

        string legacyPath = Path.Combine(root, "kyber-weave.yml");
        return File.Exists(legacyPath) ? legacyPath : null;
    }

    private static string? ReadConfigText(string configPath)
    {
        if (!File.Exists(configPath))
            return string.Empty;

        try
        {
            return File.ReadAllText(configPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WriteError($"The host configuration '{configPath}' could not be read: {ex.Message}");
            return null;
        }
    }

    private static bool WriteConfigText(string configPath, string text)
    {
        try
        {
            string? directory = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(configPath, text);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WriteError($"The host configuration '{configPath}' could not be written: {ex.Message}");
            return false;
        }
    }

    private static bool Verify(string root, string? configOption, string endpoint, bool needsKey)
    {
        KyberWeaveConfigLoadResult loaded = KyberWeaveConfigLoader.TryLoad(root, configOption);
        if (!loaded.Success || loaded.Config is null || !loaded.Config.Arbiter.Enabled)
        {
            WriteError(
                "Verification failed: the host configuration does not enable the Arbiter " +
                $"after install: {loaded.Error ?? "arbiter.enabled is not true"}.");
            return false;
        }

        if (needsKey)
        {
            string? key = null;
            try
            {
                key = ArbiterKeyResolver.Resolve(
                    endpoint,
                    ArbiterCommandComposition.CredentialStore(),
                    ArbiterCommandComposition.GetEnvironmentVariable);
            }
            catch (Exception ex) when (ex is InvalidOperationException
                or IOException
                or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception
                or ArgumentException)
            {
                key = null;
            }

            if (string.IsNullOrEmpty(key))
            {
                WriteError(
                    "Verification failed: no key resolves for the configured provider " +
                    "after install. Run with --arbiter-key, --key-stdin, or export " +
                    $"{ArbiterKeyResolver.EnvVarName}.");
                return false;
            }
        }

        ProbeBinary();
        return true;
    }

    private static void ProbeBinary()
    {
        try
        {
            ArbiterProcessProbe probe = SquadCommandComposition.ResolveArbiterProbe();
            ToolProbeResult result = probe.Probe();
            if (!result.IsAvailable)
            {
                AnsiConsole.MarkupLine(
                    "[yellow]warn:[/] The 'kyber-weave-arbiter' binary is not available: " +
                    $"{Markup.Escape(result.FailureReason ?? "not on PATH")}. " +
                    "Install the binary matching this CLI version so hooks can enforce.");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            AnsiConsole.MarkupLine(
                "[yellow]warn:[/] The 'kyber-weave-arbiter' binary probe failed: " +
                $"{Markup.Escape(ex.Message)}");
        }
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The lifecycle owns the release source and renderer lifetimes for the run.")]
    private SquadLifecycleService CreateLifecycleService(SquadStateStore stateStore)
    {
        ISquadReleaseSource releaseSource = _releaseSource ?? ResolveReleaseSource();
        ISquadRenderer renderer = _renderer ?? SquadCommandComposition.ResolveRenderer();
        ISquadGlobalRootResolver globalRoots =
            SquadCommandComposition.ResolveGlobalRoots(_environmentReader);
        return new SquadLifecycleService(
            releaseSource,
            renderer,
            stateStore,
            globalRoots: globalRoots,
            environmentReader: _environmentReader);
    }

    private ISquadReleaseSource ResolveReleaseSource()
    {
        string? local = FindLocalSquadRoot();
        if (local is not null)
            return new LocalCheckoutSquadReleaseSource(local);

        return new GitHubSquadReleaseSource(
            ReleaseOrigin.Resolve(_environmentReader ?? Environment.GetEnvironmentVariable).ApiRoot);
    }

    private static string? FindLocalSquadRoot()
    {
        try
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null)
            {
                string candidate = Path.Combine(directory.FullName, "products", "kyber-squad");
                if (File.Exists(Path.Combine(candidate, "squad.yml")))
                    return candidate;

                directory = directory.Parent;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    /// <summary>
    /// Returns <paramref name="yaml"/> with <c>arbiter.enabled</c> set to <c>true</c>.
    /// An existing <c>enabled: false</c> is flipped in place; a missing
    /// <c>arbiter:</c> block is appended. Returns the input unchanged when it
    /// already enables the arbiter. Every other byte is preserved.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>HostConfigYaml.WithArbiterEnabled</c> line for line. That helper is
    /// <c>internal</c> to <c>KyberWeave.Core</c> (visible only to tests), so this CLI
    /// command cannot call it without widening Core's surface outside this task's scope.
    /// </remarks>
    internal static string EnsureArbiterEnabled(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        string newline = yaml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        List<string> lines = SplitLines(yaml);
        int arbiter = IndexOfTopLevelKey(lines, "arbiter");
        if (arbiter < 0)
        {
            List<string> appended = [.. lines];
            if (appended.Count > 0 && appended[^1].Trim().Length > 0)
                appended.Add(string.Empty);

            appended.Add("arbiter:");
            appended.Add("  enabled: true");
            appended.Add(string.Empty);
            return string.Join(newline, appended);
        }

        int blockEnd = EndOfBlock(lines, arbiter);
        string blockIndent = BlockIndent(lines, arbiter, blockEnd);
        for (int i = arbiter + 1; i < blockEnd; i++)
        {
            if (!TrySplitKeyedLine(lines[i], "enabled", out string lineIndent, out string prefix, out string rest)
                || !string.Equals(lineIndent, blockIndent, StringComparison.Ordinal))
                continue;

            (string value, string suffix) = SplitScalarAndSuffix(rest);
            if (string.Equals(UnquoteScalar(value), "true", StringComparison.Ordinal))
                return yaml;

            string separator = prefix.EndsWith(':') ? " " : string.Empty;
            lines[i] = prefix + separator + "true" + suffix;
            return string.Join(newline, lines);
        }

        lines.Insert(arbiter + 1, blockIndent + "enabled: true");
        return string.Join(newline, lines);
    }

    /// <summary>
    /// Returns <paramref name="yaml"/> with <c>arbiter.provider.kind</c> set to
    /// <paramref name="kind"/>. The provider block is added when absent; only the
    /// kind value changes otherwise, so comments, key order, and unrelated keys
    /// survive. Returns the input unchanged when it already names the kind.
    /// </summary>
    internal static string EnsureProviderKind(string yaml, string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(yaml);

        string newline = yaml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        List<string> lines = SplitLines(yaml);
        int arbiter = IndexOfTopLevelKey(lines, "arbiter");
        if (arbiter < 0)
        {
            List<string> appended = [.. lines];
            if (appended.Count > 0 && appended[^1].Trim().Length > 0)
                appended.Add(string.Empty);

            appended.Add("arbiter:");
            appended.Add("  enabled: true");
            appended.Add("  provider:");
            appended.Add("    kind: " + kind);
            appended.Add(string.Empty);
            return string.Join(newline, appended);
        }

        int blockEnd = EndOfBlock(lines, arbiter);
        string blockIndent = BlockIndent(lines, arbiter, blockEnd);

        int provider = -1;
        string providerIndent = blockIndent;
        for (int i = arbiter + 1; i < blockEnd; i++)
        {
            if (!TrySplitKeyedLine(lines[i], "provider", out string lineIndent, out _, out _)
                || !string.Equals(lineIndent, blockIndent, StringComparison.Ordinal))
                continue;

            provider = i;
            providerIndent = lineIndent;
            break;
        }

        if (provider < 0)
        {
            int anchor = arbiter;
            for (int i = arbiter + 1; i < blockEnd; i++)
            {
                if (TrySplitKeyedLine(lines[i], "enabled", out string lineIndent, out _, out _)
                    && string.Equals(lineIndent, blockIndent, StringComparison.Ordinal))
                {
                    anchor = i;
                    break;
                }
            }

            lines.Insert(anchor + 1, blockIndent + "provider:");
            lines.Insert(anchor + 2, blockIndent + "  kind: " + kind);
            return string.Join(newline, lines);
        }

        (string providerValue, string providerSuffix) = SplitScalarAndSuffix(
            ProviderRest(lines[provider]));
        if (providerValue.Trim().Length > 0)
        {
            lines[provider] = providerIndent + "provider:" + providerSuffix;
        }

        int providerEnd = lines.Count;
        for (int i = provider + 1; i < lines.Count; i++)
        {
            string line = lines[i];
            if (line.Trim().Length == 0 || line.TrimStart()[0] == '#')
                continue;

            if (LeadingWhitespace(line).Length <= providerIndent.Length)
            {
                providerEnd = i;
                break;
            }
        }

        string subIndent = providerIndent + "  ";
        int shallowest = int.MaxValue;
        for (int i = provider + 1; i < providerEnd; i++)
        {
            string line = lines[i];
            if (line.Trim().Length == 0 || line.TrimStart()[0] == '#')
                continue;

            int length = LeadingWhitespace(line).Length;
            if (length > providerIndent.Length && length < shallowest)
                shallowest = length;
        }

        if (shallowest != int.MaxValue)
            subIndent = new string(' ', shallowest);

        for (int i = provider + 1; i < providerEnd; i++)
        {
            if (!TrySplitKeyedLine(lines[i], "kind", out string lineIndent, out string prefix, out string rest))
                continue;

            if (lineIndent.Length <= providerIndent.Length || lineIndent.Length >= 16)
                continue;

            (string value, string suffix) = SplitScalarAndSuffix(rest);
            if (string.Equals(UnquoteScalar(value), kind, StringComparison.Ordinal))
                return yaml;

            string separator = prefix.EndsWith(':') ? " " : string.Empty;
            lines[i] = prefix + separator + kind + suffix;
            return string.Join(newline, lines);
        }

        lines.Insert(provider + 1, subIndent + "kind: " + kind);
        return string.Join(newline, lines);
    }

    private static string ProviderRest(string providerLine)
    {
        if (TrySplitKeyedLine(providerLine, "provider", out _, out _, out string rest))
            return rest;

        return string.Empty;
    }

    private static List<string> SplitLines(string yaml)
    {
        string normalized = yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        List<string> lines = [.. normalized.Split('\n')];
        if (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        return lines;
    }

    private static int IndexOfTopLevelKey(List<string> lines, string name)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            if (IsTopLevelKey(lines[i], name))
                return i;
        }

        return -1;
    }

    private static bool IsTopLevelKey(string line, string name)
    {
        string text = line.Length > 0 && line[0] == '\uFEFF' ? line[1..] : line;
        if (text.Length == 0 || text[0] == ' ' || text[0] == '\t' || text[0] == '#')
            return false;

        if (!text.StartsWith(name, StringComparison.Ordinal))
            return false;

        int i = name.Length;
        if (i < text.Length && text[i] != ' ' && text[i] != '\t' && text[i] != ':' && text[i] != '#')
            return false;

        while (i < text.Length && (text[i] == ' ' || text[i] == '\t'))
            i++;

        if (i >= text.Length || text[i] != ':')
            return false;

        i++;
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t'))
            i++;

        return i >= text.Length || text[i] == '#';
    }

    private static int EndOfBlock(List<string> lines, int blockStart)
    {
        for (int i = blockStart + 1; i < lines.Count; i++)
        {
            string line = lines[i];
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] == '#')
                continue;

            return i;
        }

        return lines.Count;
    }

    private static string BlockIndent(List<string> lines, int blockStart, int blockEnd)
    {
        string? shallowest = null;
        for (int i = blockStart + 1; i < blockEnd; i++)
        {
            string trimmed = lines[i].TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#')
                continue;

            int indent = lines[i].Length - trimmed.Length;
            if (indent > 0 && (shallowest is null || indent < shallowest.Length))
                shallowest = lines[i][..indent];
        }

        return shallowest ?? "  ";
    }

    private static string LeadingWhitespace(string line)
    {
        int i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
            i++;

        return line[..i];
    }

    private static bool TrySplitKeyedLine(
        string line,
        string name,
        out string indent,
        out string prefix,
        out string rest)
    {
        indent = string.Empty;
        prefix = string.Empty;
        rest = string.Empty;

        string trimmed = line.TrimStart(' ', '\t');
        if (trimmed.StartsWith('#'))
            return false;

        indent = line[..(line.Length - trimmed.Length)];
        if (!trimmed.StartsWith(name, StringComparison.Ordinal))
            return false;

        int i = name.Length;
        if (i < trimmed.Length && trimmed[i] != ' ' && trimmed[i] != '\t' && trimmed[i] != ':')
            return false;

        while (i < trimmed.Length && (trimmed[i] == ' ' || trimmed[i] == '\t'))
            i++;

        if (i >= trimmed.Length || trimmed[i] != ':')
            return false;

        i++;
        int valueStart = i;
        while (valueStart < trimmed.Length && (trimmed[valueStart] == ' ' || trimmed[valueStart] == '\t'))
            valueStart++;

        prefix = indent + trimmed[..valueStart];
        rest = trimmed[valueStart..];
        return true;
    }

    private static (string Value, string Suffix) SplitScalarAndSuffix(string rest)
    {
        char quote = rest.Length > 0 && rest[0] is '\'' or '"' ? rest[0] : '\0';
        int start = quote == '\0' ? 0 : 1;
        for (int i = start; i < rest.Length; i++)
        {
            if (quote == '\'')
            {
                if (rest[i] == '\'')
                {
                    if (i + 1 < rest.Length && rest[i + 1] == '\'')
                    {
                        i++;
                        continue;
                    }

                    quote = '\0';
                }

                continue;
            }

            if (quote == '"')
            {
                if (rest[i] == '\\')
                {
                    i++;
                    continue;
                }

                if (rest[i] == '"')
                    quote = '\0';

                continue;
            }

            if (rest[i] == '#' && i > 0 && char.IsWhiteSpace(rest[i - 1]))
            {
                int suffixStart = i - 1;
                while (suffixStart > 0 && char.IsWhiteSpace(rest[suffixStart - 1]))
                    suffixStart--;

                return (rest[..suffixStart], rest[suffixStart..]);
            }
        }

        int valueEnd = rest.Length;
        while (valueEnd > 0 && char.IsWhiteSpace(rest[valueEnd - 1]))
            valueEnd--;

        return (rest[..valueEnd], rest[valueEnd..]);
    }

    private static string UnquoteScalar(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '\'' && trimmed[^1] == '\'')
            return trimmed[1..^1].Replace("''", "'", StringComparison.Ordinal);

        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
            return trimmed[1..^1];

        return trimmed;
    }
}
