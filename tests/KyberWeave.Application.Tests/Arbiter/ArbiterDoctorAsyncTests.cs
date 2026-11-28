using KyberWeave.Application.Arbiter;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Tests.Arbiter;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>
/// The doctor's asynchronous entry point runs every check and reports what the synchronous one reports: it resolves the
/// repository root, reads the key and asks git about the ignore rule without blocking a thread.
/// </summary>
public sealed class ArbiterDoctorAsyncTests : IDisposable
{
    private readonly InMemoryArbiterHost _host = InMemoryArbiterHost.Create();

    public void Dispose() => _host.Dispose();

    /// <summary>
    /// The asynchronous run resolves the root through the locator's asynchronous member, never the blocking one, and
    /// still reports the checks the host prints.
    /// </summary>
    [Fact]
    public async Task InspectAsync_ResolvesTheRootWithoutBlockingAndStillReportsTheChecks()
    {
        RecordingLocator locator = new(_host.WorkingDirectory);
        InMemoryArbiterHost host = InMemoryArbiterHost.Create(services =>
            services.AddSingleton<IArbiterRepositoryLocator>(locator));
        using (host)
        {
            ArbiterDoctorReport report = await host.Command<ArbiterDoctorService>().InspectAsync(
                host.WorkingDirectory, null, new DiagnosticReport(), TestContext.Current.CancellationToken);

            Assert.Equal(1, locator.AsyncCalls);
            Assert.Equal(0, locator.SyncCalls);
            Assert.False(report.ArbiterEnabled);
            Assert.Contains(report.Lines, line => line.Subject == "Key");
            Assert.Contains(report.Lines, line => line.Subject == "Log");
            Assert.Contains(report.Lines, line => line.Subject == "Binary");
            Assert.Contains(report.Lines, line => line.Subject == "Guard");
        }
    }

    /// <summary>A locator that reports the path it was given, recording which member the doctor reached.</summary>
    private sealed class RecordingLocator(string root) : IArbiterRepositoryLocator
    {
        public int SyncCalls { get; private set; }

        public int AsyncCalls { get; private set; }

        public string ResolveRepositoryRoot(string path)
        {
            SyncCalls++;
            return root;
        }

        public string ResolveArbiterDirectory(string path) => path + "/artifacts/arbiter";

        public Task<string> ResolveRepositoryRootAsync(string path, CancellationToken cancellationToken = default)
        {
            AsyncCalls++;
            return Task.FromResult(root);
        }

        public Task<string> ResolveArbiterDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(path + "/artifacts/arbiter");

        public string FindGitRoot(string start) => start;
    }
}
