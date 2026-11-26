using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Processes;
using KyberWeave.Domain.Doctor;

namespace KyberWeave.Mcp;

/// <summary>
/// The tooling check behind the docs server's tooling-check tool, for one repository root: a thin wrapper over
/// <see cref="IRepositoryDoctor"/> in ensure mode, which <c>kyber-weave doctor --ensure</c> also runs.
/// </summary>
public sealed class ToolingCheckService(
    string repositoryRoot,
    IDocsQueryService query,
    IRepositoryDoctor doctor,
    IBoundedProcessExecutor process,
    IProcessEnvironment environment) : IToolingCheckService
{
    private readonly string _repositoryRoot = repositoryRoot ?? throw new ArgumentNullException(nameof(repositoryRoot));
    private readonly IDocsQueryService _query = query ?? throw new ArgumentNullException(nameof(query));
    private readonly IRepositoryDoctor _doctor = doctor ?? throw new ArgumentNullException(nameof(doctor));
    private readonly IBoundedProcessExecutor _process = process ?? throw new ArgumentNullException(nameof(process));
    private readonly IProcessEnvironment _environment = environment ?? throw new ArgumentNullException(nameof(environment));

    /// <summary>Runs git and CodeGraph for the check; null for the default bounded by the index timeout. Tests set it so no real child starts.</summary>
    public IProcessExecutor? Executor { get; set; }

    /// <summary>The docs server executable the check starts to prove it; null for the one on <c>PATH</c>. Tests name a stub.</summary>
    public string? McpExecutable { get; set; }

    /// <inheritdoc />
    public string RunToolingCheck()
    {
        string provenance = _query.Provenance();
        DoctorOptions options = McpExecutable is null
            ? DoctorOptions.Defaults
            : DoctorOptions.Defaults with { McpExecutable = McpExecutable };
        IProcessExecutor executor = Executor ?? new BoundedExecutor(_process, options.IndexTimeout);

        DoctorReport report;
        try
        {
            report = _doctor.Run(_repositoryRoot, DoctorMode.Ensure, options, executor, _environment.GetVariable);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The check itself could not run: the caller still gets the shape it reads, with the reason and a stop.
            return string.Join('\n', provenance, "result: failed", $"FAIL doctor: {OneLine(exception.Message)}", ReturnStatus.ToolFailureLine, "EXIT: 1");
        }

        // The doctor has already applied stop-on-tool-failure to its exit code, so the code alone decides the token.
        bool stop = report.ExitCode != 0;
        StringBuilder sb = new();
        sb.AppendLine(provenance);
        sb.AppendLine(stop ? "result: failed" : "result: passed");
        foreach (string line in DoctorReportLines.For(report))
            sb.AppendLine(line);
        if (stop)
            sb.AppendLine(ReturnStatus.ToolFailureLine);
        sb.Append(CultureInfo.InvariantCulture, $"EXIT: {report.ExitCode}");
        return sb.ToString();
    }

    // The report is read line by line, so a message that spans lines is folded onto one.
    private static string OneLine(string text) =>
        string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private sealed class BoundedExecutor(IBoundedProcessExecutor process, TimeSpan timeout) : IProcessExecutor
    {
        public ProcessResult Run(ProcessStartInfo startInfo, string standardInput) =>
            process.Run(startInfo, standardInput, timeout);
    }
}
