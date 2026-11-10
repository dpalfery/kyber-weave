using System.Runtime.InteropServices;
using KyberWeave.Core.CopilotPrompt;

namespace KyberWeave.Core.Doctor;

/// <summary>
/// Doctor checks for the Copilot system-prompt override: whether the prompt file is installed and
/// valid, and whether each VS Code build's settings point at it.
/// </summary>
public static class CopilotPromptProbe
{
    /// <summary>
    /// Runs the Copilot system-prompt checks for one user.
    /// </summary>
    /// <param name="mode">The doctor run mode, which decides how strictly findings are reported.</param>
    /// <param name="homeDirectory">The user's home directory.</param>
    /// <param name="appDataDirectory">The roaming application-data directory on Windows; may be null elsewhere.</param>
    /// <param name="platform">The operating system whose VS Code layout applies.</param>
    /// <returns>The checks in report order. Empty when the override is not in use.</returns>
    public static IReadOnlyList<DoctorCheck> Run(DoctorMode mode, string homeDirectory, string? appDataDirectory, OSPlatform platform)
    {
        throw new NotImplementedException();
    }
}
