using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Doctor;
using KyberWeave.Domain.CopilotPrompt;

namespace KyberWeave.Core.Doctor;

/// <summary>The one place Core builds the doctor file-system adapter, without each caller naming the data layer.</summary>
/// <remarks>
/// The adapter reads the real file system and so lives in <c>KyberWeave.Data</c>. Core references Data, so it is the
/// lowest project both the adapter and the doctor's callers can see: it re-exports the construction here rather
/// than each probe naming the adapter class. This is the legacy wire the layering keeps until the doctor moves onto
/// the container and takes <see cref="IDoctorFileSystem"/> from it.
/// </remarks>
public static class DoctorFileSystem
{
    /// <summary>The adapter that reads the machine's real file system.</summary>
    public static IDoctorFileSystem Default { get; } = new PhysicalDoctorFileSystem();

    /// <summary>Reads one VS Code settings file through the real file system.</summary>
    /// <param name="settingsFile">The settings file path.</param>
    /// <returns>The bytes; or neither bytes nor error when the file is missing; or the reason it cannot be read.</returns>
    public static SettingsFileRead ReadSettingsFile(string settingsFile) =>
        CopilotOverrideSettings.ReadFile(
            settingsFile,
            Default.DirectoryExists,
            Default.IsPresentFile,
            Default.ReadAllBytes);
}
