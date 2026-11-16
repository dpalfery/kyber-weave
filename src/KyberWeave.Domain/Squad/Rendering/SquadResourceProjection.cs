using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>Projects a validated resource closure beside its rendered principal.</summary>
public static class SquadResourceProjection
{
    public static void Append(
        IList<SquadDeploymentFile> files,
        SquadDeploymentFile principal,
        IReadOnlyList<SquadResource> resources)
    {
        Append(files, principal.RelativePath, resources, principal.Target);
    }

    public static void Append(
        IList<SquadDeploymentFile> files,
        string principalRelativePath,
        IReadOnlyList<SquadResource> resources,
        string target)
    {
        int separator = principalRelativePath.LastIndexOf('/');
        string principalDirectory = separator < 0 ? string.Empty : principalRelativePath[..separator];
        foreach (SquadResource resource in resources)
        {
            string relativePath = principalDirectory.Length == 0
                ? resource.RelativePath
                : $"{principalDirectory}/{resource.RelativePath}";
            files.Add(new SquadDeploymentFile(
                relativePath,
                Encoding.UTF8.GetBytes(resource.Content),
                target));
        }
    }
}
