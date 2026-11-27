namespace KyberWeave.Domain.Update;

/// <summary>The github.com repository and endpoints every ordinary install reads Releases from.</summary>
internal static class GitHubReleaseEndpoints
{
    internal const string Owner = "dpalfery";
    internal const string Repo = "kyber-weave";

    internal static readonly Uri ApiRoot = new("https://api.github.com/");

    internal static readonly Uri LatestApi =
        new($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");

    internal static readonly Uri ReleasesApi =
        new($"https://api.github.com/repos/{Owner}/{Repo}/releases");

    internal static readonly Uri ReleaseDownloadBase =
        new($"https://github.com/{Owner}/{Repo}/releases/download/");
}
