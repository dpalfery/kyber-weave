using Xunit;
using System.Text;
using KyberDash.Data.Repositories;
using KyberDash.Domain;

namespace KyberDash.Data.Tests;

/// <summary>Disk-override and embedded-bundle resolution for <see cref="WebAssetRepository"/>.</summary>
public sealed class WebAssetRepositoryTests : IDisposable
{
    private readonly TempDirectory directory = new();

    /// <inheritdoc />
    public void Dispose()
    {
        WebAssetRepository.ClearEmbeddedBundleCache();
        directory.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Resolve_ServesIndexFromTheOverrideDirectory()
    {
        string webDirectory = WriteSite("index.html", "<html>ok</html>");
        WebAssetRepository repository = new();

        WebAssetDocument? document = repository.TryResolveAsset("/", webDirectory);

        Assert.NotNull(document);
        Assert.Equal("text/html; charset=utf-8", document!.ContentType);
        Assert.Equal("<html>ok</html>", Encoding.UTF8.GetString(document.Content.Span));
    }

    [Fact]
    public void Resolve_FallsBackToIndexForClientSideRoutes()
    {
        string webDirectory = WriteSite("index.html", "<html>spa</html>");
        WebAssetRepository repository = new();

        WebAssetDocument? document = repository.TryResolveAsset("/session/abc/turn/2", webDirectory);

        Assert.NotNull(document);
        Assert.Equal("<html>spa</html>", Encoding.UTF8.GetString(document!.Content.Span));
    }

    [Fact]
    public void Resolve_ServesStaticAssetsWithTheirContentType()
    {
        string webDirectory = WriteSite("index.html", "<html></html>");
        File.WriteAllText(Path.Combine(webDirectory, "app.js"), "console.log(1);");
        File.WriteAllText(Path.Combine(webDirectory, "app.css"), "body{}");
        WebAssetRepository repository = new();

        Assert.Equal("text/javascript; charset=utf-8", repository.TryResolveAsset("/app.js", webDirectory)?.ContentType);
        Assert.Equal("text/javascript; charset=utf-8", repository.TryResolveAsset("/app.js", webDirectory)?.ContentType);
        Assert.Null(repository.TryResolveAsset("/../app.css", webDirectory));
    }

    [Fact]
    public void Resolve_ReportsWhichAssetItServedSoTheHostCanAnswer404()
    {
        string webDirectory = WriteSite("index.html", "<html>spa</html>");
        Directory.CreateDirectory(Path.Combine(webDirectory, "assets"));
        File.WriteAllText(Path.Combine(webDirectory, "assets", "index-abc.js"), "console.log(1);");
        WebAssetRepository repository = new();

        Assert.Equal("assets/index-abc.js", repository.TryResolveAsset("/assets/index-abc.js", webDirectory)?.ResolvedPath);
        // A missing file falls back to the app shell, and says so: the host turns that into 404.
        Assert.Equal("index.html", repository.TryResolveAsset("/assets/missing.js", webDirectory)?.ResolvedPath);
    }

    [Fact]
    public void Resolve_RefusesDirectoryTraversal()
    {
        string webDirectory = WriteSite("index.html", "<html></html>");
        string outside = Path.Combine(directory.Path, "secret.txt");
        File.WriteAllText(outside, "top secret");
        WebAssetRepository repository = new();

        WebAssetDocument? document = repository.TryResolveAsset("/../secret.txt", webDirectory);

        Assert.Null(document);
    }

    [Fact]
    public void Resolve_ServesFromTheConfiguredDashboardDirectory()
    {
        string webDirectory = WriteSite("index.html", "<html>configured</html>");
        WebAssetRepository repository = new(webDirectory);

        WebAssetDocument? document = repository.TryResolveAsset("/");

        Assert.NotNull(document);
        Assert.Equal("<html>configured</html>", Encoding.UTF8.GetString(document!.Content.Span));
    }

    [Fact]
    public void Resolve_ReturnsNullWhenThereIsNeitherDiskNorBundle()
    {
        WebAssetRepository.ClearEmbeddedBundleCache();
        WebAssetRepository repository = new(directory.Combine("no-such-directory"));

        Assert.Null(repository.TryResolveAsset("/"));
    }

    [Fact]
    public void Resolve_IgnoresADirectoryWithoutIndexHtml()
    {
        Directory.CreateDirectory(directory.Path);
        WebAssetRepository repository = new(directory.Path);

        Assert.Null(repository.TryResolveAsset("/nested/deeper", overrideDir: null));
    }

    [Fact]
    public void ParseBundle_AcceptsAWellFormedBundle()
    {
        string bundle = CreateBundle(("index.html", "<html>bundle</html>"), ("app.js", "console.log('bundle');"));

        IReadOnlyDictionary<string, byte[]>? assets = WebAssetRepository.ParseBundle(bundle);

        Assert.NotNull(assets);
        Assert.Equal(2, assets!.Count);
        Assert.Equal("<html>bundle</html>", Encoding.UTF8.GetString(assets["index.html"]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"format\":\"other/1\",\"files\":{}}")]
    [InlineData("{\"format\":\"kyberdash-web/1\",\"files\":{}}")]
    [InlineData("{\"format\":\"kyberdash-web/1\",\"files\":{\"app.js\":\"AA==\"}}")]
    [InlineData("not json")]
    public void ParseBundle_RejectsMalformedOrIncompleteBundles(string rawJson)
    {
        Assert.Null(WebAssetRepository.ParseBundle(rawJson));
    }

    private string WriteSite(string fileName, string content)
    {
        string webDirectory = directory.Combine("site-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(webDirectory);
        File.WriteAllText(Path.Combine(webDirectory, fileName), content);
        return webDirectory;
    }

    private static string CreateBundle(params (string Path, string Content)[] files)
    {
        StringBuilder builder = new();
        builder.Append("{\"format\":\"kyberdash-web/1\",\"files\":{");
        for (int i = 0; i < files.Length; i++)
        {
            if (i > 0)
                builder.Append(',');
            builder.Append('"').Append(files[i].Path).Append("\":\"");
            builder.Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(files[i].Content)));
            builder.Append('"');
        }

        builder.Append("}}");
        return builder.ToString();
    }
}
