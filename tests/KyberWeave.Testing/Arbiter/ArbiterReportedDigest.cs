using System.Text.Json;
using KyberWeave.Cli.Commands.Arbiter;

namespace KyberWeave.Tests.Arbiter;

/// <summary>The execution digest <c>arbiter plan --format json</c> reports, for tests that need the value a task file records.</summary>
internal static class ArbiterReportedDigest
{
    /// <summary>Runs the plan command over an in-memory copy of the plan and reads its <c>execution-digest</c> metric.</summary>
    /// <param name="planPath">The plan file's absolute path; its text is copied into the in-memory file system.</param>
    internal static string Of(string planPath)
    {
        using InMemoryArbiterHost host = InMemoryArbiterHost.Create();
        _ = host.Write(planPath, File.ReadAllText(planPath));
        ArbiterCommandRun run = host.Run(() => host.Command<ArbiterPlanCommand>().Execute(
            null!,
            new ArbiterSettings { Path = planPath, Format = "json" }));
        return Read(run.Output);
    }

    /// <summary>Reads the <c>execution-digest</c> metric from a plan command's json output.</summary>
    /// <param name="output">The output, which must be one JSON document with a <c>metrics</c> object.</param>
    internal static string Read(string output)
    {
        using JsonDocument document = JsonDocument.Parse(output);
        return document.RootElement.GetProperty("metrics").GetProperty("execution-digest").GetString()!;
    }
}
