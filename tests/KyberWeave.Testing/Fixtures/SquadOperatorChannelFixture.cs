using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests.Squad;

internal static class SquadOperatorChannelFixture
{
    internal const string GateVariable = "KYBER_SQUAD_OPERATOR_CHANNEL";
    internal const string ServerVariable = "KYBER_SQUAD_OPERATOR_CHANNEL_SERVER";
    internal const string CopilotHomeVariable = "COPILOT_HOME";
    internal const string DefaultServerName = "ops-relay";
    internal const string ToolEntry = "'ops-relay/*'";
    internal const string Placeholder = "{{operator-channel-server}}";
    internal const string ServerAbsentWarning = "operator-channel-server-absent";
    internal const string NameMissingWarning = "operator-channel-server-name-missing";
    internal const string NameInvalidWarning = "operator-channel-server-name-invalid";
    internal const string NotRenderedDegradation = "operator-channel-not-rendered";
    internal const int ConductorBodyLimit = 30000;

    internal const string ServerPresentConfig =
        "{\"mcpServers\":{\"ops-relay\":{\"command\":\"ops-relay-server\",\"args\":[]}}}";

    internal static readonly string[] EventLabels =
    [
        "Hard-block question.",
        "Final report.",
        "Blocked or failed run status."
    ];

    internal static readonly string[] NonTrueGateValues =
    [
        "",
        " ",
        "True",
        "TRUE",
        "1",
        "yes",
        "false",
        " true",
        "true "
    ];

    internal static readonly string ProductRoot =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    internal static readonly string OverlayPath =
        Path.Combine(ProductRoot, "agents", "conductor", "overlays", "operator-channel.md");

    private static readonly Regex ToolsFlowSequence =
        new(@"^tools:\s*\[(?<items>.*)\]\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>A mcp-config.json that declares exactly one server, <paramref name="serverName"/>.</summary>
    internal static string ServerConfig(string serverName)
    {
        return "{\"mcpServers\":{\"" + serverName + "\":{\"command\":\"x\",\"args\":[]}}}";
    }

    /// <summary>Creates an in-memory environment variable reader delegate for operator channel settings.</summary>
    internal static Func<string, string?> CreateEnvironmentReader(
        string? gateValue,
        string? copilotHome = null,
        string? serverName = DefaultServerName)
    {
        return key => key switch
        {
            GateVariable => gateValue,
            ServerVariable => serverName,
            CopilotHomeVariable => copilotHome,
            _ => null
        };
    }

    /// <summary>
    /// Resolves global roots so that the Copilot global root is the given COPILOT_HOME directory, the
    /// same location the probe reads <c>mcp-config.json</c> from.
    /// </summary>
    internal static ISquadGlobalRootResolver GlobalRoots(string copilotHome, string homeDirectory)
    {
        return new SquadGlobalRoots(
            name => string.Equals(name, CopilotHomeVariable, StringComparison.Ordinal) ? copilotHome : null,
            homeDirectory);
    }

    internal static async Task<SquadRenderResult> RenderAsync(
        string copilotHome,
        string homeDirectory,
        string? gateValue,
        IReadOnlyList<SquadTarget> targets,
        SquadDeploymentScope scope,
        string? serverName = DefaultServerName,
        SquadSource? preloadedSource = null)
    {
        ISquadRenderer renderer = SquadHostDefaults.ResolveRenderer();
        SquadRenderResult result = await renderer.RenderAsync(new SquadRenderRequest(
            ProductRoot,
            targets,
            scope,
            GlobalRoots: GlobalRoots(copilotHome, homeDirectory),
            PreloadedSource: preloadedSource ?? TestSquadCorpus.CanonicalSource,
            EnvironmentReader: CreateEnvironmentReader(gateValue, copilotHome, serverName)).Prepared());
        Assert.True(result.Success, string.Join("; ", result.Errors));
        return result;
    }

    internal static IReadOnlyList<string> FileDigests(SquadRenderResult result)
    {
        return result.Files
            .Select(static file => $"{file.RelativePath} {Convert.ToHexString(SHA256.HashData(file.Content.Span))}")
            .ToArray();
    }

    internal static string[] FileTexts(SquadRenderResult result)
    {
        return result.Files
            .Select(static file => Encoding.UTF8.GetString(file.Content.Span))
            .ToArray();
    }

    internal static string ConductorText(SquadRenderResult result)
    {
        SquadDeploymentFile file = Assert.Single(
            result.Files,
            static f => f.RelativePath.EndsWith("conductor.agent.md", StringComparison.Ordinal));
        return Encoding.UTF8.GetString(file.Content.Span);
    }

    /// <summary>The entries of the conductor frontmatter <c>tools:</c> flow sequence, in order.</summary>
    internal static IReadOnlyList<string> ToolEntries(string markdown)
    {
        Match match = ToolsFlowSequence.Match(markdown);
        Assert.True(match.Success, "The conductor frontmatter has no `tools:` flow sequence.");
        return match.Groups["items"].Value
            .Split(',')
            .Select(static item => item.Trim())
            .ToArray();
    }

    /// <remarks>
    /// <paramref name="requiredDegradationCode"/> names the one degradation a fact expects, such as
    /// <c>operator-channel-not-rendered</c>. Only degradations carrying that exact code are skipped,
    /// because the code itself contains <c>operator-channel</c>. Every other degradation, every
    /// generated file, and every warning is still scanned, so leakage elsewhere still fails.
    /// </remarks>
    internal static string[] OperatorChannelMentions(
        SquadRenderResult result,
        string? requiredDegradationCode = null)
    {
        IEnumerable<string> texts = result.Files
            .Select(static file => Encoding.UTF8.GetString(file.Content.Span))
            .Concat(result.Degradations
                .Where(d => d.Code != requiredDegradationCode)
                .Select(static d => $"{d.Target} {d.Code} {d.Details}"))
            .Concat(result.Warnings.Select(static w => $"{w.Code} {w.Message} {w.Target}"));

        return texts
            .Where(static text => text.Contains("operator-channel", StringComparison.Ordinal))
            .ToArray();
    }

    internal static string OverlayText()
    {
        Assert.True(File.Exists(OverlayPath), $"The operator-channel overlay is missing at {OverlayPath}.");
        return File.ReadAllText(OverlayPath, Encoding.UTF8);
    }

    /// <summary>
    /// The overlay's first prose paragraph, read from the file with the placeholder replaced by the default
    /// server name, so the body check follows the file as the renderer will emit it.
    /// </summary>
    internal static string OverlayFirstParagraph()
    {
        return OverlayText()
            .Split('\n')
            .Select(static line => line.Trim())
            .First(static line => line.Length > 0 && !line.StartsWith('#'))
            .Replace(Placeholder, DefaultServerName, StringComparison.Ordinal);
    }
}
