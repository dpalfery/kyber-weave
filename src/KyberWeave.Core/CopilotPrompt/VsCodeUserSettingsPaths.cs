using System.Runtime.InteropServices;

namespace KyberWeave.Core.CopilotPrompt;

/// <summary>
/// The two VS Code builds that can receive the Copilot system-prompt override.
/// </summary>
public enum VsCodeFlavour
{
    /// <summary>VS Code stable, which keeps its user data under the <c>Code</c> directory.</summary>
    Stable,

    /// <summary>VS Code Insiders, which keeps its user data under the <c>Code - Insiders</c> directory.</summary>
    Insiders,
}

/// <summary>
/// Locates each VS Code build's user data directory and its default-profile <c>settings.json</c>.
/// </summary>
/// <remarks>
/// The override setting is application scope, so VS Code reads it only from the default
/// profile's user settings file and applies it machine-wide. Paths are computed from the
/// supplied home directory, application-data directory and platform rather than from the
/// running process, so each platform's layout can be exercised from any host.
/// </remarks>
public static class VsCodeUserSettingsPaths
{
    /// <summary>
    /// The user setting that holds the per-model capability overrides, including the prompt override file.
    /// </summary>
    public const string SettingId = "chat.agentHost.copilot.modelCapabilityOverrides";

    /// <summary>
    /// Returns the path of the default profile's user <c>settings.json</c> for one VS Code build.
    /// </summary>
    /// <param name="flavour">The VS Code build whose settings file is wanted.</param>
    /// <param name="homeDirectory">The user's home directory.</param>
    /// <param name="appDataDirectory">The roaming application-data directory on Windows; may be null elsewhere.</param>
    /// <param name="platform">The operating system whose layout applies.</param>
    /// <returns>The absolute path <c>&lt;user-data&gt;/User/settings.json</c>.</returns>
    public static string SettingsFile(VsCodeFlavour flavour, string homeDirectory, string? appDataDirectory, OSPlatform platform)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Returns the user-data directory that one VS Code build uses on the given platform.
    /// </summary>
    /// <param name="flavour">The VS Code build whose user-data directory is wanted.</param>
    /// <param name="homeDirectory">The user's home directory.</param>
    /// <param name="appDataDirectory">The roaming application-data directory on Windows; may be null elsewhere.</param>
    /// <param name="platform">The operating system whose layout applies.</param>
    /// <returns>The absolute user-data directory for the build.</returns>
    public static string UserDataDirectory(VsCodeFlavour flavour, string homeDirectory, string? appDataDirectory, OSPlatform platform)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Lists the VS Code builds whose user-data directory is present on this machine.
    /// </summary>
    /// <param name="homeDirectory">The user's home directory.</param>
    /// <param name="appDataDirectory">The roaming application-data directory on Windows; may be null elsewhere.</param>
    /// <param name="platform">The operating system whose layout applies.</param>
    /// <returns>The detected builds, in a stable order. Empty when neither build is installed.</returns>
    public static IReadOnlyList<VsCodeFlavour> Detect(string homeDirectory, string? appDataDirectory, OSPlatform platform)
    {
        throw new NotImplementedException();
    }
}
