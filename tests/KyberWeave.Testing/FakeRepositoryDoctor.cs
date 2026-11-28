using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Services;
using KyberWeave.Domain.Doctor;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Tests;

/// <summary>Answers a fixed doctor report and records every argument the caller passed.</summary>
internal sealed record DoctorRun(
    string Root,
    DoctorMode Mode,
    DoctorOptions Options,
    IProcessRunner Executor,
    Func<string, string?> Environment,
    string? LockDirectory);

/// <summary>Answers a fixed doctor report and records what it was asked to inspect.</summary>
internal sealed class FakeRepositoryDoctor : IRepositoryDoctor
{
    private readonly List<DoctorRun> _runs = [];

    /// <summary>The runs the caller asked for, in order, with the arguments it passed.</summary>
    public IReadOnlyList<DoctorRun> Runs => _runs;

    /// <summary>The report every run answers. Its exit code is what the caller must return.</summary>
    public DoctorReport Report { get; init; } = new(".", DoctorMode.Check, 0, []);

    /// <inheritdoc />
    public Task<DoctorReport> RunAsync(
        string root,
        DoctorMode mode,
        DoctorOptions options,
        IProcessRunner executor,
        Func<string, string?> environment,
        string? lockDirectory = null,
        CancellationToken cancellationToken = default)
    {
        _runs.Add(new DoctorRun(root, mode, options, executor, environment, lockDirectory));
        return Task.FromResult(Report with { Root = root, Mode = mode });
    }

    /// <inheritdoc />
    public Task<DoctorReport> RunPostCheckoutAsync(
        string root,
        DoctorOptions options,
        IProcessRunner executor,
        string? lockDirectory = null,
        CancellationToken cancellationToken = default)
    {
        _runs.Add(new DoctorRun(root, DoctorMode.Ensure, options, executor, _ => null, lockDirectory));
        return Task.FromResult(Report with { Root = root, Mode = DoctorMode.Ensure });
    }
}
