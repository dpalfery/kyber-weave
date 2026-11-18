using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Services;
using KyberWeave.Domain.Doctor;

namespace KyberWeave.Tests;

/// <summary>Answers a fixed doctor report and records what it was asked to inspect.</summary>
internal sealed class FakeRepositoryDoctor : IRepositoryDoctor
{
    private readonly List<(string Root, DoctorMode Mode)> _runs = [];

    /// <summary>The runs the caller asked for, in order.</summary>
    public IReadOnlyList<(string Root, DoctorMode Mode)> Runs => _runs;

    /// <summary>The report every run answers.</summary>
    public DoctorReport Report { get; init; } = new(".", DoctorMode.Check, 0, []);

    /// <inheritdoc />
    public DoctorReport Run(
        string root,
        DoctorMode mode,
        DoctorOptions options,
        IProcessExecutor executor,
        Func<string, string?> environment,
        string? lockDirectory = null)
    {
        _runs.Add((root, mode));
        return Report with { Root = root, Mode = mode };
    }

    /// <inheritdoc />
    public DoctorReport RunPostCheckout(
        string root,
        DoctorOptions options,
        IProcessExecutor executor,
        string? lockDirectory = null)
    {
        _runs.Add((root, DoctorMode.Provision));
        return Report with { Root = root, Mode = DoctorMode.Provision };
    }
}
