using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.FileSystem;
using KyberWeave.Core.Squad.Packaging;
using KyberWeave.Core.Squad.Validation;
using KyberWeave.Application.Update;

namespace KyberWeave.Application.Squad.Packaging;

/// <summary>Packages the canonical Kyber-Squad source from the repository root into the APM and Agent Plugins archives.</summary>
/// <param name="environment">Names the working directory the repository root is looked for in.</param>
/// <param name="paths">Resolves a relative output directory against the working directory.</param>
/// <param name="locator">Finds the canonical source.</param>
/// <param name="packager">Writes the archives and checks the composed tree.</param>
public sealed class SquadPackService(
    IProcessEnvironment environment,
    IPathResolver paths,
    ISquadSourceLocator locator,
    ISquadPackager packager) : ISquadPackService
{
    /// <inheritdoc />
    public SquadPackOutcome Pack(SquadPackRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string format = request.Format;
        if (!string.Equals(format, "apm", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(format, "plugins", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(format, "all", StringComparison.OrdinalIgnoreCase))
        {
            return Refused(SquadPackStatus.InvalidFormat);
        }

        string workingDirectory = environment.CurrentDirectory;
        string? sourcePath = locator.Resolve(workingDirectory);
        if (sourcePath is null)
        {
            return Refused(SquadPackStatus.NotRepositoryRoot);
        }

        string version = !string.IsNullOrWhiteSpace(request.Version)
            ? ReleaseVersion.Normalize(request.Version)
            : request.DefaultVersion;
        string outDir = string.IsNullOrWhiteSpace(request.OutDirectory)
            ? Path.Combine(workingDirectory, "artifacts")
            : paths.GetFullPath(request.OutDirectory, workingDirectory);

        List<string> warnings = [];
        try
        {
            packager.ValidateSource(sourcePath);

            // The archives carry products/kyber-squad plus .apm/skills, so the composed tree is what gets loaded.
            // Loading only the Squad tree would let a bad companion skill through to install time. The root is
            // resolved inside the try, so a refused symbolic link is a failure with its message, as the other refusals are.
            string? companionSkillsRoot = packager.ResolveCompanionSkillsRoot(sourcePath);
            packager.ValidateComposedTree(sourcePath, companionSkillsRoot, version, warnings.Add);

            if (string.Equals(format, "apm", StringComparison.OrdinalIgnoreCase))
            {
                string apmArchive = packager.PackApm(sourcePath, outDir, version, companionSkillsRoot);
                return new SquadPackOutcome(SquadPackStatus.Packed, null, apmArchive, null, null, warnings);
            }

            if (string.Equals(format, "plugins", StringComparison.OrdinalIgnoreCase))
            {
                string pluginsArchive = packager.PackPlugins(sourcePath, outDir, version, companionSkillsRoot);
                return new SquadPackOutcome(SquadPackStatus.Packed, null, null, pluginsArchive, null, warnings);
            }

            (string apm, string plugins, string checksum) = packager.PackAll(sourcePath, outDir, version, companionSkillsRoot);
            return new SquadPackOutcome(SquadPackStatus.Packed, null, apm, plugins, checksum, warnings);
        }
        catch (Exception ex) when (ex is SquadSourceValidationException or InvalidDataException or InvalidOperationException || FileFailure.Is(ex))
        {
            return new SquadPackOutcome(SquadPackStatus.Failed, ex.Message, null, null, null, warnings);
        }
    }

    private static SquadPackOutcome Refused(SquadPackStatus status) => new(status, null, null, null, null, []);
}
