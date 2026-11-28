using System.Diagnostics;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Host;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Processes;
using KyberWeave.Domain.Doctor;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Doctor;

/// <summary>
///     Checks the repository's documentation tooling: the CodeGraph index and the live docs MCP server.
/// </summary>
/// <remarks>
///     <para>
///         Mode resolution follows D11. <c>--check</c> is always read-only. <c>--provision</c> always provisions.
///         <c>--ensure</c> prepares the index and then proves both tools, and is the one command a worker runs before
///         it starts work. A bare run provisions only when stdin is a terminal and <c>CI</c> is unset. Otherwise it
///         acts as <c>--check</c> and says so, so an unattended run never writes an index by surprise.
///     </para>
///     <para>
///         The terminal and environment are injected, so the mode choice is testable without a real console. The
///         production defaults are the actual stdin and process environment.
///     </para>
/// </remarks>
public sealed class DoctorCommand : AsyncCommand<DoctorSettings>
{
    /// <summary>The line a bare, non-interactive run prints so its user knows how to provision.</summary>
    internal const string NonInteractiveHint = "provisioning skipped: non-interactive; rerun with --provision";

    private readonly IRepositoryDoctor _doctor;
    private readonly IProcessEnvironment _environment;
    private readonly IConsole _console;
    private readonly IPathResolver _paths;
    private readonly IBoundedProcessExecutor _processes;
    private readonly IProcessExecutor? _executor;
    private readonly string _mcpExecutable;

    /// <summary>Creates the command over the doctor and the process it reads its mode from.</summary>
    /// <param name="doctor">Runs the checks.</param>
    /// <param name="environment">Says whether stdin is a terminal and whether <c>CI</c> is set.</param>
    /// <param name="console">Where the command writes its report and its refusals.</param>
    /// <param name="paths">Resolves the path argument against the working directory.</param>
    /// <param name="processes">Runs the CodeGraph child, bounded by the index timeout, when no executor is given.</param>
    /// <param name="executor">Runs the CodeGraph child; a process executor bounded by the index timeout when none is registered.</param>
    /// <param name="mcpExecutable">The MCP executable; the one on <c>PATH</c> when none is given. Tests name a stub so they never spawn the real server.</param>
    public DoctorCommand(
        IRepositoryDoctor doctor,
        IProcessEnvironment environment,
        IConsole console,
        IPathResolver paths,
        IBoundedProcessExecutor processes,
        IProcessExecutor? executor = null,
        string? mcpExecutable = null)
    {
        ArgumentNullException.ThrowIfNull(doctor);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(processes);

        _doctor = doctor;
        _environment = environment;
        _console = console;
        _paths = paths;
        _processes = processes;
        _executor = executor;
        _mcpExecutable = mcpExecutable ?? DoctorOptions.DefaultMcpExecutableName;
    }

    /// <summary>How many of the mutually exclusive mode options the caller gave: check, provision, ensure and the hook's post-checkout mode.</summary>
    private static int ModesRequested(DoctorSettings settings) =>
        (settings.Check ? 1 : 0) + (settings.Provision ? 1 : 0) + (settings.Ensure ? 1 : 0) + (settings.PostCheckout ? 1 : 0);

    /// <inheritdoc />
    public override async Task<int> ExecuteAsync(CommandContext context, DoctorSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (ModesRequested(settings) > 1)
        {
            await _console.Error.WriteLineAsync("kyber-weave: error: choose one of --check, --provision, --ensure or --post-checkout, not more than one.").ConfigureAwait(false);
            return 2;
        }

        if (settings.IndexTimeoutSeconds <= 0 || settings.McpTimeoutSeconds <= 0)
        {
            await _console.Error.WriteLineAsync("kyber-weave: error: --index-timeout and --mcp-timeout must be positive seconds.").ConfigureAwait(false);
            return 2;
        }

        Func<string, string?> environment = _environment.GetVariable;
        bool terminal = !_environment.IsInputRedirected;
        bool ci = !string.IsNullOrEmpty(environment("CI"));

        DoctorMode mode;
        bool hint = false;
        if (settings.PostCheckout)
            mode = DoctorMode.Ensure;
        else if (settings.Check)
            mode = DoctorMode.Check;
        else if (settings.Ensure)
            mode = DoctorMode.Ensure;
        else if (settings.Provision)
            mode = DoctorMode.Provision;
        else if (terminal && !ci)
            mode = DoctorMode.Provision;
        else
        {
            mode = DoctorMode.Check;
            hint = true;
        }

        DoctorOptions options = new(
            TimeSpan.FromSeconds(settings.IndexTimeoutSeconds),
            TimeSpan.FromSeconds(settings.McpTimeoutSeconds),
            DoctorOptions.DefaultCodeGraphExecutable,
            _mcpExecutable);

        string root;
        try
        {
            root = _paths.GetFullPath(settings.Path, _environment.CurrentDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            // The path is client input, and an unusable one is a refusal: exit 2, never an exception. Callers
            // branch on the 0/1/2/3 contract, so a stack trace would be indistinguishable from a broken doctor.
            await _console.Error.WriteLineAsync($"kyber-weave: error: '{settings.Path}' is not a usable path.").ConfigureAwait(false);
            return 2;
        }

        IProcessRunner executor = _executor as IProcessRunner ?? new BoundedExecutor(_processes, options.IndexTimeout);
        // The hook's own mode skips the MCP and Copilot probes and installs nothing, so it is routed apart from the doctor run.
        DoctorReport report = settings.PostCheckout
            ? await _doctor.RunPostCheckoutAsync(root, options, executor, cancellationToken: cancellationToken).ConfigureAwait(false)
            : await _doctor.RunAsync(root, mode, options, executor, environment, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (hint && !settings.Json)
            await _console.Out.WriteLineAsync(NonInteractiveHint).ConfigureAwait(false);

        DoctorReportRenderer.Write(_console.Out, report, settings.Json);
        return report.ExitCode;
    }

    private sealed class BoundedExecutor : IProcessRunner
    {
        private readonly IBoundedProcessExecutor _process;
        private readonly IProcessRunner _runner;
        private readonly TimeSpan _timeout;

        public BoundedExecutor(IBoundedProcessExecutor process, TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(process);

            _process = process;
            _runner = process as IProcessRunner
                ?? throw new InvalidOperationException("The bounded process executor must also be an IProcessRunner.");
            _timeout = timeout;
        }

        public ProcessResult Run(ProcessStartInfo startInfo, string standardInput) =>
            _process.Run(startInfo, standardInput, _timeout);

        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default) =>
            _runner.RunAsync(request, cancellationToken);

        public bool IsAlive(int processId) => _runner.IsAlive(processId);

        public string? FindOnPath(string program) => _runner.FindOnPath(program);
    }
}
