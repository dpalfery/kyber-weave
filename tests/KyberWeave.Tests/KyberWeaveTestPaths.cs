namespace KyberWeave.Tests;

/// <summary>Locates KyberWeave source paths from test output directories.</summary>
internal static class KyberWeaveTestPaths
{
    public static string ToolRoot { get; } = LocateToolRoot();

    public static string McpProjectPath =>
        Path.Combine(ToolRoot, "src", "KyberWeave.Mcp", "KyberWeave.Mcp.csproj");

    public static string McpDocsToolsSourcePath =>
        Path.Combine(ToolRoot, "src", "KyberWeave.Mcp", "DocsTools.cs");

    private static string LocateToolRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "KyberWeave.sln")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate KyberWeave.sln from the test output directory.");
    }
}
