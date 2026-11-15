using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Domain;

namespace KyberDash.Application;

/// <summary>
/// Resolves dashboard SPA assets, honouring an explicit disk override before falling back to the
/// embedded bundle (<c>kyberdash web</c> static file serving).
/// </summary>
/// <remarks>
/// The override directory comes from the request when the host names one, otherwise from
/// <see cref="DashOptions.DashboardDir"/>, otherwise from the repository's own resolution. That order
/// lets a developer serve a freshly built bundle without changing the operator's configuration.
/// </remarks>
public class WebAssetService : IWebAssetService
{
    private readonly IWebAssetRepository assets;
    private readonly string? dashboardDir;

    /// <summary>
    /// Initializes a new <see cref="WebAssetService"/>.
    /// </summary>
    /// <param name="assets">Repository resolving disk and embedded SPA assets.</param>
    /// <param name="options">Composition options carrying an optional dashboard directory override.</param>
    public WebAssetService(IWebAssetRepository assets, DashOptions options)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(options);

        this.assets = assets;
        dashboardDir = string.IsNullOrWhiteSpace(options.DashboardDir) ? null : Path.GetFullPath(options.DashboardDir);
    }

    /// <summary>Dashboard directory override in force, when the composition set one.</summary>
    public string? DashboardDirectory => dashboardDir;

    /// <inheritdoc />
    public WebAssetDocument? TryResolveAsset(string requestPath, string? overrideDir = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestPath);

        string? resolvedOverride = !string.IsNullOrWhiteSpace(overrideDir)
            ? overrideDir
            : dashboardDir;

        return assets.TryResolveAsset(requestPath, resolvedOverride);
    }
}
