using System.Globalization;
using System.Text;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Processes;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// Scripted process seam: every run is answered from a table keyed by program and arguments, and every call is
/// recorded. Nothing is started, so a unit test controls the result, the timeout and the failure exactly.
/// A run with no matching script fails with <see cref="UnscriptedProcessCallException"/> instead of reporting
/// success, so a typo in the code under test cannot pass — and production code that treats an absent resource as an
/// absent fact cannot swallow the failure, because no product catch names that type.
/// </summary>
public sealed class ScriptedProcessRunner : IProcessRunner
{
    private readonly List<Answer> _scripts = [];
    private readonly List<ProcessRunRequest> _calls = [];
    private readonly List<ProcessRunRequest> _timedOutCalls = [];
    private readonly HashSet<string> _missingDirectories = new(StringComparer.Ordinal);
    private readonly Dictionary<int, bool> _alive = [];
    private readonly Dictionary<string, string?> _onPath = new(StringComparer.Ordinal);

    /// <summary>Every call the code under test made, in order.</summary>
    public IReadOnlyList<ProcessRunRequest> Calls => _calls;

    /// <summary>The calls whose scripted result reported a timeout.</summary>
    public IReadOnlyList<ProcessRunRequest> TimedOutCalls => _timedOutCalls;

    /// <summary>Answers one program and exact argument list with a result.</summary>
    /// <param name="program">The program name the script matches.</param>
    /// <param name="result">The result to return.</param>
    /// <param name="arguments">The exact arguments the script matches; omit to match any arguments.</param>
    /// <returns>This runner, for chaining.</returns>
    public ScriptedProcessRunner Script(string program, ProcessRunResult result, params string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(result);
        _scripts.Add(new Answer(program, arguments.Length == 0 ? null : arguments, result, Exception: null, Respond: null));
        return this;
    }

    /// <summary>Answers one program and exact argument list from a responder that sees the whole request.</summary>
    /// <remarks>
    /// Use this when the answer must depend on the working directory, the environment, standard input, the timeout
    /// or the output limit. A fixed <see cref="ProcessRunResult"/> cannot prove that the caller passed those fields.
    /// </remarks>
    /// <param name="program">The program name the script matches.</param>
    /// <param name="respond">Builds the result from the request the seam received.</param>
    /// <param name="arguments">The exact arguments the script matches; omit to match any arguments.</param>
    /// <returns>This runner, for chaining.</returns>
    public ScriptedProcessRunner Script(
        string program,
        Func<ProcessRunRequest, ProcessRunResult> respond,
        params string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(respond);
        _scripts.Add(new Answer(program, arguments.Length == 0 ? null : arguments, Result: null, Exception: null, respond));
        return this;
    }

    /// <summary>Answers one program and argument list as a killed, timed-out run.</summary>
    /// <param name="program">The program name the script matches.</param>
    /// <param name="arguments">The exact arguments the script matches; omit to match any arguments.</param>
    /// <returns>This runner, for chaining.</returns>
    public ScriptedProcessRunner ScriptTimeout(string program, params string[] arguments) =>
        Script(
            program,
            new ProcessRunResult(-1, string.Empty, TimedOut: true) { StandardOutput = string.Empty, StandardError = string.Empty },
            arguments);

    /// <summary>Makes one program and argument list fail with an exception.</summary>
    /// <param name="program">The program name the script matches.</param>
    /// <param name="exception">The exception the run throws.</param>
    /// <param name="arguments">The exact arguments the script matches; omit to match any arguments.</param>
    /// <returns>This runner, for chaining.</returns>
    public ScriptedProcessRunner Throws(string program, Exception exception, params string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(exception);
        _scripts.Add(new Answer(program, arguments.Length == 0 ? null : arguments, Result: null, exception, Respond: null));
        return this;
    }

    /// <summary>Makes one program fail as a program that is not installed.</summary>
    /// <param name="program">The program name the script matches.</param>
    /// <returns>This runner, for chaining.</returns>
    public ScriptedProcessRunner WithMissingProgram(string program) =>
        Throws(program, new FileNotFoundException($"command not found: {program}", program));

    /// <summary>Makes a run in this directory fail as a missing working directory.</summary>
    /// <param name="directory">The working directory the script matches.</param>
    /// <returns>This runner, for chaining.</returns>
    public ScriptedProcessRunner WithMissingDirectory(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        _ = _missingDirectories.Add(directory);
        return this;
    }

    /// <summary>Reports a process id as running or ended.</summary>
    /// <param name="processId">The process id to answer for.</param>
    /// <param name="alive">Whether it is running.</param>
    /// <returns>This runner, for chaining.</returns>
    public ScriptedProcessRunner WithAlive(int processId, bool alive = true)
    {
        _alive[processId] = alive;
        return this;
    }

    /// <summary>Reports a program as found on the path.</summary>
    /// <param name="program">The program name to answer for.</param>
    /// <param name="path">The full path it resolves to.</param>
    /// <returns>This runner, for chaining.</returns>
    public ScriptedProcessRunner WithProgramOnPath(string program, string path)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(path);
        _onPath[program] = path;
        return this;
    }

    /// <inheritdoc />
    public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _calls.Add(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.WorkingDirectory is { } directory && _missingDirectories.Contains(directory))
        {
            throw new DirectoryNotFoundException($"working directory not found: {directory}");
        }

        Answer script = _scripts.Find(candidate => candidate.Matches(request))
                        ?? throw new UnscriptedProcessCallException(
                            $"No process result is scripted for '{request.Program}' with arguments [{string.Join(", ", request.Arguments)}]. " +
                            "Script it with Script, ScriptTimeout or Throws.");

        if (script.Exception is { } exception)
        {
            throw exception;
        }

        ProcessRunResult result = script.Respond is { } respond ? respond(request) : script.Result!;
        if (request.MaxOutputBytes is { } maxOutputBytes &&
            (Encoding.UTF8.GetByteCount(result.StandardOutput) > maxOutputBytes ||
             Encoding.UTF8.GetByteCount(result.StandardError) > maxOutputBytes))
        {
            throw new OutputLimitExceededException(
                $"The process output exceeded the limit of {maxOutputBytes.ToString(CultureInfo.InvariantCulture)} bytes.");
        }

        if (result.TimedOut)
        {
            _timedOutCalls.Add(request);
        }

        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public bool IsAlive(int processId) => _alive.TryGetValue(processId, out bool alive) && alive;

    /// <inheritdoc />
    public string? FindOnPath(string program)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(program);
        return _onPath.TryGetValue(program, out string? path) ? path : null;
    }

    private sealed record Answer(
        string Program,
        string[]? Arguments,
        ProcessRunResult? Result,
        Exception? Exception,
        Func<ProcessRunRequest, ProcessRunResult>? Respond)
    {
        public bool Matches(ProcessRunRequest request) =>
            string.Equals(Program, request.Program, StringComparison.Ordinal) &&
            (Arguments is null || Arguments.SequenceEqual(request.Arguments, StringComparer.Ordinal));
    }
}
