using System.Reflection;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Mcp;
using ModelContextProtocol.Server;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Calls the documentation gate tools by their MCP names, the way a client does, so the in-memory cases and the real
/// git cases drive the same two tools through the same reflection.
/// </summary>
internal static class McpGateToolInvoker
{
    internal const string ValidateToolName = "docs_validate_governed_document_schema";
    internal const string DriftToolName = "docs_detect_drift_between_documents_and_code";

    internal static string InvokeValidate(
        DocsTools tools,
        bool mergeReady,
        string? gitBase = null,
        int charBudget = DocumentIndex.DefaultCharBudget)
    {
        MethodInfo method = FindMcpTool(ValidateToolName);
        return InvokeByParameterNames(
            tools,
            method,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["mergeReady"] = mergeReady,
                ["base"] = gitBase,
                ["charBudget"] = charBudget
            });
    }

    internal static string InvokeDrift(DocsTools tools, int charBudget = DocumentIndex.DefaultCharBudget)
    {
        MethodInfo method = FindMcpTool(DriftToolName);
        return InvokeByParameterNames(
            tools,
            method,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["charBudget"] = charBudget });
    }

    internal static string InvokeByParameterNames(
        DocsTools tools,
        MethodInfo method,
        IReadOnlyDictionary<string, object?> arguments)
    {
        object?[] args = method.GetParameters().Select(parameter =>
        {
            if (arguments.TryGetValue(parameter.Name!, out object? value))
                return value ?? (parameter.ParameterType.IsValueType
                    ? Activator.CreateInstance(parameter.ParameterType)
                    : null);

            if (parameter.HasDefaultValue) return parameter.DefaultValue;

            throw new InvalidOperationException(
                $"Missing argument '{parameter.Name}' for MCP tool '{method.GetCustomAttribute<McpServerToolAttribute>()?.Name}'.");
        }).ToArray();

        return (string)method.Invoke(tools, args)!;
    }

    internal static MethodInfo FindMcpTool(string toolName)
    {
        MethodInfo? method = typeof(DocsTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SingleOrDefault(candidate =>
                candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName);
        Assert.True(method is not null, $"DocsTools must expose an MCP tool named '{toolName}'.");
        return method!;
    }

    internal static string ResultLine(string response)
    {
        string[] lines = response.Split('\n');
        Assert.True(lines.Length >= 2, "Gate tool responses must include a result line after provenance.");
        return lines[1].TrimEnd('\r');
    }

    internal static bool CanCreateSymbolicLinks()
    {
        string probeDirectory = Path.Combine(Path.GetTempPath(), "kw-mcp-symlink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probeDirectory);
        try
        {
            string target = Path.Combine(probeDirectory, "target.txt");
            File.WriteAllText(target, "probe");
            File.CreateSymbolicLink(Path.Combine(probeDirectory, "link.txt"), target);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        finally
        {
            if (Directory.Exists(probeDirectory))
                Directory.Delete(probeDirectory, true);
        }
    }
}
