using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using KyberDash.Application;
using KyberDash.Contracts.Services;
using KyberDash.Core;
using KyberDash.Core.Exceptions;
using KyberDash.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace KyberDash.Cli;

/// <summary>
/// Entry point for <c>kyberdash</c>: a thin router that parses arguments and delegates every
/// command to a <c>KyberDash.Contracts.Services</c> interface.
/// </summary>
/// <remarks>
/// This assembly deliberately holds no engine behaviour. It resolves services from the container
/// (or an injected provider, in tests), prints what they return, and starts the sibling executables
/// for the modes that are separate processes.
/// </remarks>
public static partial class Program
{

    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    /// <summary>Runs the <c>kyberdash</c> command line.</summary>
    /// <param name="args">The process command line.</param>
    /// <returns>The process exit code the router decides on.</returns>
    public static async Task<int> Main(string[] args)
    {
        using CancellationTokenSource shutdown = new();
        ConsoleCancelEventHandler onCancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        using PosixSignalRegistration sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            shutdown.Cancel();
        });

        try
        {
            return await RunAsync(args, Console.Out, Console.Error, shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    /// <summary>
    /// Runs one invocation against explicit streams, resolving services from the application container.
    /// </summary>
    /// <param name="args">The process command line.</param>
    /// <param name="output">Where command output is written.</param>
    /// <param name="error">Where diagnostics are written.</param>
    /// <param name="cancellationToken">Stops long-running commands.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        await using ServiceProvider provider = BuildProvider(args);
        return await RunAsync(args, output, error, provider, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one invocation against explicit streams and an explicit service provider.
    /// </summary>
    /// <param name="args">The process command line.</param>
    /// <param name="output">Where command output is written.</param>
    /// <param name="error">Where diagnostics are written.</param>
    /// <param name="provider">The container command services resolve from.</param>
    /// <param name="processMode">How to run the separate <c>web</c>, <c>mcp</c>, and <c>otel</c> processes; defaults to starting siblings.</param>
    /// <param name="cancellationToken">Stops long-running commands.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        IServiceProvider provider,
        IProcessMode? processMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(provider);

        if (args.Length == 0)
            return await ReportAsync(provider, output, []).ConfigureAwait(false);

        string first = args[0];
        if (first is "--version" or "-v" or "-V")
        {
            await output.WriteLineAsync(ResolveVersion()).ConfigureAwait(false);
            return 0;
        }

        if (first is "--help" or "-h")
        {
            await WriteHelpAsync(output).ConfigureAwait(false);
            return 0;
        }

        string[] rest = args[1..];
        return first switch
        {
            "report" => await ReportAsync(provider, output, rest).ConfigureAwait(false),
            "doctor" => await DoctorAsync(provider, output, rest).ConfigureAwait(false),
            "menubar" => await RunMenubarAsync(provider, output, error, rest, cancellationToken).ConfigureAwait(false),
            "engine" => await RunEngineAsync(provider, output, error, rest, cancellationToken).ConfigureAwait(false),
            "e2e-seed" => RunE2eSeed(provider, output, error, rest),
            "web" => await SpawnAsync(output, error, processMode ?? new SiblingProcessMode(), "web", rest, cancellationToken).ConfigureAwait(false),
            "mcp" => await SpawnAsync(output, error, processMode ?? new SiblingProcessMode(), "mcp", rest, cancellationToken).ConfigureAwait(false),
            "otel" => await SpawnAsync(output, error, processMode ?? new SiblingProcessMode(), "otel", rest, cancellationToken).ConfigureAwait(false),
            "kyber" => await RunKyberGroupAsync(provider, output, error, rest, processMode, cancellationToken).ConfigureAwait(false),
            _ => await FailUnknownCommandAsync(error, first).ConfigureAwait(false),
        };
    }

    /// <summary>Builds the container every command resolves its services from.</summary>
    /// <param name="args">The process command line, read for <c>--db</c> and <c>--state-dir</c>.</param>
    /// <returns>The service provider; the caller disposes it.</returns>
    public static ServiceProvider BuildProvider(string[]? args = null)
    {
        ServiceCollection services = new();
        DashOptions options = new();

        foreach (string[] pair in ReadGlobalOptions(args))
        {
            if (string.Equals(pair[0], "--db", StringComparison.Ordinal))
                options.DatabasePath = Path.GetFullPath(pair[1]);
            else
                options.StateDirectory = Path.GetFullPath(pair[1]);
        }

        services.AddDashApplication(options);
        return services.BuildServiceProvider();
    }

    /// <summary>Resolves the reported version from the assembly's informational version.</summary>
    public static string ResolveVersion()
    {
        return DashVersion.Current;
    }

    private static IEnumerable<string[]> ReadGlobalOptions(string[]? args)
    {
        if (args is null)
            yield break;

        for (int index = 0; index < args.Length; index++)
        {
            string arg = args[index];
            if (arg is ("--db" or "--state-dir") && index + 1 < args.Length)
            {
                yield return [arg, args[++index]];
            }
        }
    }

    private static async Task WriteHelpAsync(TextWriter output)
    {
        await output.WriteLineAsync(
            "kyberdash - KyberDash context health collector, dashboard server, and analytics MCP CLI\n\n"
            + "Commands:\n"
            + "  report                   Show context health report (default)\n"
            + "  doctor                   Inspect canonical store health (--json for machine output)\n"
            + "  web                      Start the local dashboard and engine (spawns kyberdash-api)\n"
            + "  mcp                      Serve read-only analytics over MCP stdio (spawns kyberdash-mcp)\n"
            + "  otel                     Start the standalone OTLP/HTTP receiver\n"
            + "  menubar                  Install, update, quit, launch, or count the desktop tray app (--quit, --launch, --running)\n"
            + "  engine ensure|stop|verify  Replace, stop, or check the running engine\n"
            + "  e2e-seed                 Seed an isolated store for Playwright E2E worlds\n"
            + "  kyber <subcommand>       Store, capture, and statusline utilities\n\n"
            + "Options:\n"
            + "  --version, -v            Print the version\n"
            + "  --help, -h               Print this help").ConfigureAwait(false);
    }

    private static async Task<int> FailUnknownCommandAsync(TextWriter error, string command)
    {
        await error.WriteLineAsync($"kyberdash: unknown command '{command}'. Run `kyberdash --help` for usage.").ConfigureAwait(false);
        return 1;
    }

    private static async Task<int> ReportAsync(IServiceProvider provider, TextWriter output, string[] args)
    {
        string format = "text";
        string? harness = null;
        string? session = null;
        string? run = null;
        int limit = 5;

        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--format" when index + 1 < args.Length:
                    format = args[++index];
                    break;
                case "--harness" when index + 1 < args.Length:
                    harness = args[++index];
                    break;
                case "--session" when index + 1 < args.Length:
                    session = args[++index];
                    break;
                case "--run" when index + 1 < args.Length:
                    run = args[++index];
                    break;
                case "--limit" when index + 1 < args.Length:
                    if (int.TryParse(args[++index], NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed > 0)
                        limit = parsed;
                    break;
            }
        }

        IReportService reports = provider.GetRequiredService<IReportService>();
        (int exitCode, string rendered) = reports.GenerateReport(format, harness, session, run, limit);
        await output.WriteLineAsync(rendered).ConfigureAwait(false);
        return exitCode;
    }

    private static async Task<int> DoctorAsync(IServiceProvider provider, TextWriter output, string[] args)
    {
        bool asJson = args.Contains("--json", StringComparer.Ordinal);
        IDoctorService doctor = provider.GetRequiredService<IDoctorService>();
        DoctorDiagnosticsReport report = doctor.RunDiagnostics();

        if (asJson)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(
                new
                {
                    databasePath = report.DatabasePath,
                    records = report.Records,
                    sessions = report.Sessions,
                    observedHarnesses = report.ObservedHarnesses,
                    circuitBreakerState = report.CircuitBreakerState,
                    lastRebuild = report.LastRebuild is { } rebuild
                        ? new
                        {
                            databasePath = rebuild.DatabasePath,
                            oldSchemaVersion = rebuild.OldSchemaVersion,
                            newSchemaVersion = rebuild.NewSchemaVersion,
                            settingsResetToDefaults = rebuild.SettingsResetToDefaults,
                            unrecoverableHistoryClasses = rebuild.UnrecoverableHistoryClasses,
                            reingestTriggered = rebuild.ReingestTriggered,
                        }
                        : null,
                },
                IndentedJsonOptions)).ConfigureAwait(false);
            return 0;
        }

        string harnesses = report.ObservedHarnesses.Count == 0 ? "none" : string.Join(", ", report.ObservedHarnesses);
        await output.WriteLineAsync($"KyberDash Doctor ({report.DatabasePath})").ConfigureAwait(false);
        await output.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"Records: {report.Records}  Sessions: {report.Sessions}  Harnesses: {harnesses}  Circuit breaker: {report.CircuitBreakerState}")).ConfigureAwait(false);
        if (report.LastRebuild is { } lastRebuild)
        {
            string unrecoverable = string.Join(", ", lastRebuild.UnrecoverableHistoryClasses);
            string reingest = lastRebuild.ReingestTriggered ? "triggered" : "pending the next job-host start";
            await output.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"Last rebuild: schema {lastRebuild.OldSchemaVersion} -> {lastRebuild.NewSchemaVersion} at '{lastRebuild.DatabasePath}'; settings reset to defaults; unrecoverable history: {unrecoverable}; re-ingest {reingest}")).ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>Usage line for <c>kyberdash menubar</c>; the tray verbs the update orchestrates are part of it.</summary>
    private const string MenubarUsage =
        "Usage: kyberdash menubar [--force] [--update] [--version <v>] | --quit | --launch | --running";

    private static async Task<int> RunMenubarAsync(
        IServiceProvider provider,
        TextWriter output,
        TextWriter error,
        string[] args,
        CancellationToken cancellationToken)
    {
        bool force = args.Contains("--force", StringComparer.Ordinal);
        bool update = args.Contains("--update", StringComparer.Ordinal);
        bool quit = args.Contains("--quit", StringComparer.Ordinal);
        bool launch = args.Contains("--launch", StringComparer.Ordinal);
        bool running = args.Contains("--running", StringComparer.Ordinal);
        string? requestedVersion = null;
        bool unknown = false;
        for (int index = 0; index < args.Length; index++)
        {
            string arg = args[index];
            if (arg == "--version" && index + 1 < args.Length)
            {
                requestedVersion = args[++index];
            }
            else if (arg is "--db" or "--state-dir" && index + 1 < args.Length)
            {
                // Global options are read by the container builder; they are skipped here with their value.
                index++;
            }
            else if (arg is not ("--force" or "--update" or "--quit" or "--launch" or "--running"))
            {
                unknown = true;
            }
        }

        int verbs = (quit ? 1 : 0) + (launch ? 1 : 0) + (running ? 1 : 0);
        bool mixed = verbs > 1 || (verbs == 1 && (force || update || requestedVersion is not null));
        if (unknown || mixed)
        {
            await error.WriteLineAsync(MenubarUsage).ConfigureAwait(false);
            return 2;
        }

        IMenubarService menubar = provider.GetRequiredService<IMenubarService>();
        MenubarInstallOutcome outcome = quit
            ? await menubar.QuitAsync(cancellationToken).ConfigureAwait(false)
            : launch
                ? await menubar.LaunchAsync(cancellationToken).ConfigureAwait(false)
                : running
                    ? await menubar.CountRunningAsync(cancellationToken).ConfigureAwait(false)
                    : await menubar
                        .InstallOrUpdateAsync(force, update, requestedVersion, ResolveVersion(), cancellationToken)
                        .ConfigureAwait(false);

        TextWriter destination = outcome.ExitCode == 0 ? output : error;
        await destination.WriteLineAsync(outcome.Message).ConfigureAwait(false);
        return outcome.ExitCode;
    }

    /// <summary>Usage line for <c>kyberdash engine</c>. Its wording is part of the contract: T3 reads it.</summary>
    private const string EngineUsage =
        "Usage: kyberdash engine ensure --if-running / stop / verify --version <v> [--wait <seconds>] [--state-dir <dir>]";

    private sealed record EngineOptions(string? StateDir, string? Version, int WaitSeconds);

    private static async Task<int> RunEngineAsync(
        IServiceProvider provider,
        TextWriter output,
        TextWriter error,
        string[] args,
        CancellationToken cancellationToken)
    {
        string verb = args.Length > 0 ? args[0] : string.Empty;
        if (verb is not ("ensure" or "stop" or "verify"))
        {
            await error.WriteLineAsync(EngineUsage).ConfigureAwait(false);
            return 2;
        }

        IEngineLifecycleService lifecycle = provider.GetRequiredService<IEngineLifecycleService>();
        if (verb == "ensure")
        {
            return await RunEngineEnsureAsync(lifecycle, output, error, args, cancellationToken).ConfigureAwait(false);
        }

        if (!TryReadEngineOptions(verb, args, out EngineOptions options, out string problem))
        {
            await error.WriteLineAsync(problem).ConfigureAwait(false);
            return 2;
        }

        if (verb == "stop")
        {
            EngineEnsureReport stop = await lifecycle
                .StopRunningEngineAsync(options.StateDir, cancellationToken)
                .ConfigureAwait(false);
            return await WriteEngineReportAsync(stop, output, error, stop.Succeeded ? 0 : 1).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(options.Version))
        {
            await error.WriteLineAsync("kyberdash engine verify: --version <v> is required.").ConfigureAwait(false);
            return 2;
        }

        // Probes are PollInterval apart, so the wait in seconds becomes whole probes; at least one probe always runs.
        double probes = Math.Ceiling(options.WaitSeconds / EngineLifecycleService.PollInterval.TotalSeconds);
        int attempts = (int)Math.Clamp(probes, 1, int.MaxValue);
        EngineEnsureReport verify = await lifecycle
            .VerifyRunningEngineVersionAsync(options.Version, attempts, options.StateDir, cancellationToken)
            .ConfigureAwait(false);
        int exitCode = verify.Outcome switch
        {
            EngineVerifyOutcome.Running => 0,
            EngineVerifyOutcome.OtherVersion => 3,
            EngineVerifyOutcome.Unresponsive => 4,
            _ => 1,
        };
        return await WriteEngineReportAsync(verify, output, error, exitCode).ConfigureAwait(false);
    }

    private static async Task<int> RunEngineEnsureAsync(
        IEngineLifecycleService lifecycle,
        TextWriter output,
        TextWriter error,
        string[] args,
        CancellationToken cancellationToken)
    {
        if (!args.Contains("--if-running", StringComparer.Ordinal))
        {
            await error.WriteLineAsync(
                "kyberdash engine ensure: only `--if-running` is supported (it never starts an engine on its own).").ConfigureAwait(false);
            return 2;
        }

        string? stateDir = null;
        for (int index = 1; index < args.Length; index++)
        {
            if (args[index] == "--state-dir" && index + 1 < args.Length)
                stateDir = Path.GetFullPath(args[++index]);
        }

        EngineEnsureReport report = await lifecycle
            .EnsureRunningEngineMatchesVersionAsync(
                ResolveVersion(),
                stateDir,
                SiblingProcessMode.SiblingExecutable("kyberdash-api"),
                cancellationToken)
            .ConfigureAwait(false);

        return await WriteEngineReportAsync(report, output, error, report.Succeeded ? 0 : 1).ConfigureAwait(false);
    }

    /// <summary>Reads the options of <c>engine stop</c> and <c>engine verify</c>; any other token is a usage error.</summary>
    private static bool TryReadEngineOptions(string verb, string[] args, out EngineOptions options, out string problem)
    {
        string? stateDir = null;
        string? version = null;
        int waitSeconds = 20;
        for (int index = 1; index < args.Length; index++)
        {
            bool hasValue = index + 1 < args.Length;
            if (args[index] == "--state-dir" && hasValue)
            {
                stateDir = Path.GetFullPath(args[++index]);
            }
            else if (args[index] == "--version" && hasValue)
            {
                version = args[++index];
            }
            else if (args[index] == "--wait" && hasValue
                && int.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int seconds)
                && seconds >= 0)
            {
                waitSeconds = seconds;
                index++;
            }
            else
            {
                options = new EngineOptions(null, null, 0);
                problem = $"kyberdash engine {verb}: unknown or incomplete option '{args[index]}'.";
                return false;
            }
        }

        options = new EngineOptions(stateDir, version, waitSeconds);
        problem = string.Empty;
        return true;
    }

    private static async Task<int> WriteEngineReportAsync(
        EngineEnsureReport report,
        TextWriter output,
        TextWriter error,
        int exitCode)
    {
        TextWriter destination = report.Succeeded ? output : error;
        await destination.WriteLineAsync(report.Message).ConfigureAwait(false);
        return exitCode;
    }

    private static int RunE2eSeed(IServiceProvider provider, TextWriter output, TextWriter error, string[] args)
    {
        string? world = null;
        string? databasePath = null;
        for (int index = 0; index < args.Length; index++)
        {
            if (args[index] == "--world" && index + 1 < args.Length)
                world = args[++index];
            else if (args[index] == "--db" && index + 1 < args.Length)
                databasePath = args[++index];
        }

        if (string.IsNullOrWhiteSpace(world) || string.IsNullOrWhiteSpace(databasePath))
        {
            error.WriteLine("Usage: kyberdash e2e-seed --world <harness-collection|factory-replay> --db <path>");
            return 2;
        }

        IE2eSeederService seeder = provider.GetRequiredService<IE2eSeederService>();
        int exitCode = seeder.SeedWorld(world, Path.GetFullPath(databasePath), ResolveRepositoryRoot());
        if (exitCode != 0)
            error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"kyberdash e2e-seed: world '{world}' failed with exit code {exitCode}."));

        return exitCode;
    }

    private static async Task<int> RunKyberGroupAsync(
        IServiceProvider provider,
        TextWriter output,
        TextWriter error,
        string[] args,
        IProcessMode? processMode,
        CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            await error.WriteLineAsync(
                "Usage: kyberdash kyber <otel|refresh|import-history|build|clean|settings|capture|antigravity-statusline|vacuum>").ConfigureAwait(false);
            return 2;
        }

        string subcommand = args[0];
        string[] rest = args[1..];
        switch (subcommand)
        {
            case "otel":
                return await SpawnAsync(output, error, processMode ?? new SiblingProcessMode(), "otel", rest, cancellationToken).ConfigureAwait(false);
            case "refresh":
            case "import-history":
            case "build":
                return await ProjectAsync(provider, output, rest).ConfigureAwait(false);
            case "clean":
                return await CleanAsync(provider, output, error, rest).ConfigureAwait(false);
            case "settings":
                return await SettingsAsync(provider, output, rest).ConfigureAwait(false);
            case "capture":
                return await CaptureAsync(provider, output, error, rest, cancellationToken).ConfigureAwait(false);
            case "antigravity-statusline":
                return await AntigravityStatusLineAsync(provider, cancellationToken).ConfigureAwait(false);
            case "vacuum":
                return await VacuumAsync(provider, output, rest).ConfigureAwait(false);
            default:
                return await FailUnknownCommandAsync(error, $"kyber {subcommand}").ConfigureAwait(false);
        }
    }

    private static async Task<int> ProjectAsync(IServiceProvider provider, TextWriter output, string[] args)
    {
        _ = args;
        ICanonProjectionService projection = provider.GetRequiredService<ICanonProjectionService>();
        (ProjectionPassReport pass, IReadOnlyList<HarnessRollupRow> rollups) = projection.ProjectAll();
        await output.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"Projected: built={pass.Built}, pruned={pass.Pruned}, findings={pass.Findings}, rollups={rollups.Count}")).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> CleanAsync(IServiceProvider provider, TextWriter output, TextWriter error, string[] args)
    {
        bool all = false;
        bool confirmed = false;
        List<string> harnesses = [];
        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--all":
                    all = true;
                    break;
                case "--yes":
                    confirmed = true;
                    break;
                case "--harness" when index + 1 < args.Length:
                    harnesses.Add(args[++index]);
                    break;
            }
        }

        if (!confirmed)
        {
            await error.WriteLineAsync("kyberdash kyber clean: pass --yes to confirm irreversible wipe.").ConfigureAwait(false);
            return 2;
        }

        ICleanService clean = provider.GetRequiredService<ICleanService>();
        CleanReport report = clean.CleanDatabase(new CleanScope(
            All: all,
            Harnesses: harnesses.Count > 0 ? harnesses : null));
        await output.WriteLineAsync(JsonSerializer.Serialize(report)).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> SettingsAsync(IServiceProvider provider, TextWriter output, string[] args)
    {
        bool asJson = args.Contains("--json", StringComparer.Ordinal);
        ISettingsService settings = provider.GetRequiredService<ISettingsService>();
        SettingsView view = settings.GetSettings();

        if (asJson)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(view)).ConfigureAwait(false);
            return 0;
        }

        await output.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"dashboardPort={view.DashboardPort} receiverHosted={view.ReceiverHosted} refreshCadenceMinutes={view.RefreshCadenceMinutes} jobsPaused={view.JobsPaused} folderImportScheduled={view.FolderImportScheduled}")).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> CaptureAsync(
        IServiceProvider provider,
        TextWriter output,
        TextWriter error,
        string[] args,
        CancellationToken cancellationToken)
    {
        ICaptureService capture = provider.GetRequiredService<ICaptureService>();

        if (args.Length >= 2 && args[0] == "plugin" && args[1] == "install")
        {
            string? harness = null;
            bool ifInstalled = false;
            string? configDir = null;
            for (int index = 2; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--harness" when index + 1 < args.Length:
                        harness = args[++index];
                        break;
                    case "--if-installed":
                        ifInstalled = true;
                        break;
                    case "--preserve":
                        break;
                    case "--config-dir" when index + 1 < args.Length:
                        configDir = args[++index];
                        break;
                }
            }

            if (harness is null)
            {
                await error.WriteLineAsync("kyberdash capture plugin install: --harness missing. Expected one of: opencode, factory.").ConfigureAwait(false);
                return 2;
            }

            (int exitCode, string message) = capture.InstallPlugin(harness, ifInstalled, configDir);
            TextWriter destination = exitCode == 0 ? output : error;
            await destination.WriteLineAsync(message).ConfigureAwait(false);
            return exitCode;
        }

        if (args.Length >= 2 && args[0] == "plugin" && args[1] == "consent")
        {
            bool on = args.Contains("--on", StringComparer.Ordinal);
            bool off = args.Contains("--off", StringComparer.Ordinal);
            string? harness = null;
            for (int index = 2; index < args.Length; index++)
            {
                if (args[index] == "--harness" && index + 1 < args.Length)
                    harness = args[++index];
            }

            if (harness is null || on == off)
            {
                await error.WriteLineAsync("Usage: kyberdash kyber capture plugin consent --harness factory --on|--off").ConfigureAwait(false);
                return 2;
            }

            (int exitCode, string message) = capture.SetFactoryConsent(on);
            TextWriter destination = exitCode == 0 ? output : error;
            await destination.WriteLineAsync(message).ConfigureAwait(false);
            return exitCode;
        }

        _ = cancellationToken;
        await output.WriteLineAsync(
            "kyberdash kyber capture: the OTLP capture receiver runs inside `kyberdash web` / `kyberdash otel`.").ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// Records an Antigravity statusline payload. Always exits 0 and keeps stdout silent: the host
    /// disables a statusline hook that fails or prints.
    /// </summary>
    private static async Task<int> AntigravityStatusLineAsync(IServiceProvider provider, CancellationToken cancellationToken)
    {
        string raw = await Console.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
            return 0;

        ICaptureService capture = provider.GetRequiredService<ICaptureService>();
        try
        {
            await capture.RecordAntigravityStatusLineAsync(raw, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            await Console.Error.WriteLineAsync($"kyberdash: antigravity-statusline could not record the payload: {ex.Message}").ConfigureAwait(false);
        }

        return 0;
    }

    private static async Task<int> VacuumAsync(IServiceProvider provider, TextWriter output, string[] args)
    {
        IDoctorService doctor = provider.GetRequiredService<IDoctorService>();
        if (args.Contains("--json", StringComparer.Ordinal))
            return await DoctorAsync(provider, output, args).ConfigureAwait(false);

        if (!doctor.RunVacuum())
        {
            await output.WriteLineAsync("kyberdash kyber vacuum: the store is not a SQLite file; vacuum does not apply.").ConfigureAwait(false);
            return 0;
        }

        await output.WriteLineAsync("kyberdash kyber vacuum: checkpoint and compaction complete.").ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> SpawnAsync(
        TextWriter output,
        TextWriter error,
        IProcessMode processMode,
        string mode,
        string[] args,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processMode);

        ProcessInvocation invocation = mode switch
        {
            "web" => new ProcessInvocation(SiblingProcessMode.SiblingExecutable("kyberdash-api"), ["web", .. args], forwardStandardInput: false),
            "mcp" => new ProcessInvocation(SiblingProcessMode.SiblingExecutable("kyberdash-mcp"), args, forwardStandardInput: true),
            _ => new ProcessInvocation(SiblingProcessMode.SiblingExecutable("kyberdash-api"), ["--otlp-only", .. args], forwardStandardInput: false),
        };

        int exitCode = await processMode
            .RunAsync(invocation, output, error, cancellationToken)
            .ConfigureAwait(false);
        return exitCode;
    }

    private static string ResolveRepositoryRoot()
    {
        string? current = AppContext.BaseDirectory;
        for (int depth = 0; depth < 8 && current is not null; depth++)
        {
            if (File.Exists(Path.Combine(current, "KyberWeave.sln")))
                return current;

            current = Path.GetDirectoryName(current);
        }

        return Directory.GetCurrentDirectory();
    }
}
