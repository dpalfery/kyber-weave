using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Data.Docs.Closeout;
using KyberWeave.Data.Docs.FileSystem;

namespace KyberWeave.Tests;

/// <summary>The physical ports the documentation engine's classes take, shared by the tests that run them over a temporary checkout.</summary>
internal static class DocsPorts
{
    internal static readonly IRepositoryFileStore Files = new PhysicalRepositoryFileStore();
    internal static readonly IBoundedProcessExecutor Process = new KernelBoundedProcessExecutor();

    internal static readonly IExclusiveFileWriter Exclusive = new ExclusiveFileWriter();

    internal static IPlanTaskService Tasks => new PlanTaskService(Files);
}
