using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;
using KyberDash.Contracts.Services;
using Xunit;

namespace KyberDash.Presentation.Tests;

/// <summary>
/// Enforces the layering rules the presentation projects promise: no data access, no repository
/// types, no downward references from the inner layers, and no sealed types anywhere in
/// <c>KyberDash.*</c>.
/// </summary>
/// <remarks>
/// These tests read compiled assemblies and the project files on disk. A reference added to a
/// <c>.csproj</c> file shows up in the assembly graph even when no code uses it yet, so checking
/// both catches a reference that is wired but not yet exercised.
/// </remarks>
public class ArchitectureBoundaryTests
{
    private static readonly Assembly ApiAssembly = typeof(KyberDash.Api.Program).Assembly;
    private static readonly Assembly McpAssembly = typeof(KyberDash.Mcp.Program).Assembly;
    private static readonly Assembly CliAssembly = typeof(KyberDash.Cli.Program).Assembly;
    private static readonly Assembly ContractsAssembly = typeof(DashOptions).Assembly;
    private static readonly Assembly DomainAssembly = typeof(KyberDash.Domain.ServerInfo).Assembly;
    private static readonly Assembly CoreAssembly = typeof(KyberDash.Core.Exceptions.DashException).Assembly;

    [Fact]
    public void PresentationAssembliesNeverReferenceData()
    {
        foreach (Assembly assembly in new[] { ApiAssembly, McpAssembly, CliAssembly })
        {
            IReadOnlyList<string> referenced = ReferencedAssemblyNames(assembly);
            Assert.DoesNotContain("KyberDash.Data", referenced, StringComparer.Ordinal);
        }
    }

    [Fact]
    public void PresentationProjectFilesDoNotReferenceData()
    {
        string? root = TryFindPresentationDirectory();
        if (root is null)
            return;

        foreach (string project in new[] { "KyberDash.Api", "KyberDash.Mcp", "KyberDash.Cli" })
        {
            string path = Path.Combine(root, project, project + ".csproj");
            Assert.True(File.Exists(path), $"Project file not found: {path}");

            IReadOnlyList<string> references = ProjectReferenceNames(path);
            Assert.DoesNotContain("KyberDash.Data", references, StringComparer.Ordinal);
            Assert.DoesNotContain("KyberDash.Contracts.Repositories", references, StringComparer.Ordinal);
        }
    }

    [Fact]
    public void PresentationProjectFilesReferenceOnlyThePermittedLayers()
    {
        string? root = TryFindPresentationDirectory();
        if (root is null)
            return;

        string[] permitted = ["KyberDash.Application", "KyberDash.Contracts", "KyberDash.Core", "KyberDash.Domain"];

        foreach (string project in new[] { "KyberDash.Api", "KyberDash.Mcp", "KyberDash.Cli" })
        {
            string path = Path.Combine(root, project, project + ".csproj");
            foreach (string reference in ProjectReferenceNames(path))
            {
                Assert.True(
                    permitted.Contains(reference, StringComparer.Ordinal),
                    $"{project} references '{reference}', which is outside the presentation tier's permitted references ({string.Join(", ", permitted)}).");
            }
        }
    }

    [Fact]
    public void NoPresentationTypeTakesOrDeclaresARepositoryContract()
    {
        foreach (Assembly assembly in new[] { ApiAssembly, McpAssembly, CliAssembly })
        {
            foreach (Type type in ExportedTypes(assembly))
            {
                foreach (ConstructorInfo constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    foreach (ParameterInfo parameter in constructor.GetParameters())
                    {
                        Assert.False(
                            IsRepositoryContract(parameter.ParameterType),
                            $"{type.FullName} constructor parameter '{parameter.Name}' is typed '{parameter.ParameterType.FullName}': presentation must not depend on repository contracts.");
                    }
                }

                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                {
                    Assert.False(
                        IsRepositoryContract(field.FieldType),
                        $"{type.FullName} field '{field.Name}' is typed '{field.FieldType.FullName}': presentation must not depend on repository contracts.");
                }

                foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                {
                    Assert.False(
                        IsRepositoryContract(property.PropertyType),
                        $"{type.FullName} property '{property.Name}' is typed '{property.PropertyType.FullName}': presentation must not depend on repository contracts.");
                }
            }
        }
    }

    [Fact]
    public void DomainNeverReferencesOuterLayers()
    {
        AssertNoReferences(DomainAssembly, "KyberDash.Data", "KyberDash.Application", "kyberdash-api", "kyberdash-mcp", "kyberdash");
    }

    [Fact]
    public void DataNeverReferencesOuterLayers()
    {
        Assembly? data = LoadOptionalAssembly("KyberDash.Data");
        if (data is null)
            return;

        AssertNoReferences(data, "KyberDash.Application", "kyberdash-api", "kyberdash-mcp", "kyberdash");
    }

    [Fact]
    public void ContractsAndCoreDoNotReferencePresentation()
    {
        AssertNoReferences(ContractsAssembly, "KyberDash.Data", "KyberDash.Application", "kyberdash-api", "kyberdash-mcp", "kyberdash");
        AssertNoReferences(CoreAssembly, "KyberDash.Data", "KyberDash.Application", "kyberdash-api", "kyberdash-mcp", "kyberdash");
    }

    [Fact]
    public void NoKyberDashClassOrRecordIsSealed()
    {
        List<string> sealedTypes = [];

        // An assembly can be loaded twice by reflection: explicitly, and through one of its
        // references. Duplicates are not findings.
        HashSet<Assembly> scanned = [];

        // The presentation assemblies are the ones this worker owns, and the rule is absolute for
        // them. The inner layers are checked too, except where a mocking or test library has
        // generated a sealed type inside the assembly under test.
        foreach (Assembly assembly in new[] { ApiAssembly, McpAssembly, CliAssembly })
        {
            if (!scanned.Add(assembly))
                continue;

            sealedTypes.AddRange(SealedTypes(assembly, allowGenerated: false).Select(t => $"{assembly.GetName().Name}: {t.FullName}"));
        }

        foreach (Assembly assembly in new[] { ContractsAssembly, DomainAssembly, CoreAssembly })
        {
            if (!scanned.Add(assembly))
                continue;

            sealedTypes.AddRange(SealedTypes(assembly, allowGenerated: true).Select(t => $"{assembly.GetName().Name}: {t.FullName}"));
        }

        Assembly? data = LoadOptionalAssembly("KyberDash.Data");
        if (data is not null && scanned.Add(data))
            sealedTypes.AddRange(SealedTypes(data, allowGenerated: true).Select(t => $"KyberDash.Data: {t.FullName}"));

        Assembly? application = LoadOptionalAssembly("KyberDash.Application");
        if (application is not null && scanned.Add(application))
            sealedTypes.AddRange(SealedTypes(application, allowGenerated: true).Select(t => $"KyberDash.Application: {t.FullName}"));

        Assert.Empty(sealedTypes);
    }

    [Fact]
    public void ApiMapsMinimalApiEndpointsAndNoControllers()
    {
        string? root = TryFindPresentationDirectory();
        if (root is null)
            return;

        IEnumerable<string> fileNames = SourceFiles(Path.Combine(root, "KyberDash.Api"));
        List<string> offenders = [];

        foreach (string file in fileNames)
        {
            string text = File.ReadAllText(file);
            if (text.Contains("[ApiController]", StringComparison.Ordinal)
                || text.Contains("AddControllers(", StringComparison.Ordinal)
                || text.Contains("ControllerBase", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The maintenance-named whole-store read stays out of every request path.
    /// <see cref="KyberDash.Contracts.Repositories.IRelationalStoreReader.AllSessionRecordsForMaintenance"/>
    /// materializes every stored record and child row in one query, so on a large store it is the
    /// very stall the purpose-named reads exist to remove.
    /// </summary>
    /// <remarks>
    /// Only the projector's full pass, the <c>kyber build</c> command, and doctor may name it.
    /// The request paths — the engine's bridge, the rollups engine, and the dashboard, analytics,
    /// and report services — must answer from the purpose-named reads instead.
    /// </remarks>
    [Fact]
    public void RequestPathsCannotReachMaintenanceReads()
    {
        string? root = TryFindPresentationDirectory();
        if (root is null)
            return;

        const string maintenanceRead = "AllSessionRecordsForMaintenance";

        // The contract declaration, its adapter, and the maintenance consumers allowed to name it.
        string[] permitted =
        [
            Path.Combine("KyberDash.Contracts", "Repositories", "IRelationalStoreReader.cs"),
            Path.Combine("KyberDash.Data", "Repositories", "SqliteRelationalStore.Reader.cs"),
            Path.Combine("KyberDash.Application", "CanonSessionProjectorEngine.cs"),
            Path.Combine("KyberDash.Cli", "Program.cs"),
            Path.Combine("KyberDash.Application", "Services", "DoctorService.cs"),
        ];

        List<string> offenders = [];
        foreach (string file in SourceFiles(root))
        {
            if (!File.ReadAllText(file).Contains(maintenanceRead, StringComparison.Ordinal))
                continue;

            string relative = Path.GetRelativePath(root, file);
            if (!permitted.Contains(relative, StringComparer.Ordinal))
                offenders.Add(relative);
        }

        Assert.True(
            offenders.Count == 0,
            $"Only the projection's full pass, the CLI build path, and doctor may name {maintenanceRead}; these sources name it: {string.Join(", ", offenders)}.");
    }

    private static void AssertNoReferences(Assembly assembly, params string[] forbiddenNames)
    {
        IReadOnlyList<string> referenced = ReferencedAssemblyNames(assembly);
        foreach (string forbidden in forbiddenNames)
        {
            Assert.DoesNotContain(forbidden, referenced, StringComparer.Ordinal);
        }
    }

    private static IReadOnlyList<string> ReferencedAssemblyNames(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(n => n.Name ?? string.Empty).ToList();

    private static IReadOnlyList<Type> ExportedTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null).Select(t => t!).ToList();
        }
    }

    private static IEnumerable<Type> SealedTypes(Assembly assembly, bool allowGenerated)
    {
        foreach (Type type in ExportedTypes(assembly))
        {
            if (!type.IsClass || type.IsValueType || type.IsEnum)
                continue;

            if (typeof(Delegate).IsAssignableFrom(type))
                continue;

            // Only the product's own types: pulling in a test-host or mocking delegate is not a
            // finding about KyberDash code.
            if (type.Namespace is null || !type.Namespace.StartsWith("KyberDash.", StringComparison.Ordinal))
                continue;

            // Compiler-synthesised types (lambdas, anonymous types, iterators) are never ours to seal.
            if (type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
                continue;

            // Outside the presentation tier, a type carrying a mocking or test-host attribute is
            // generated on behalf of a library, not authored here.
            if (allowGenerated && type.GetCustomAttributes(inherit: false).OfType<Attribute>().Any(IsGeneratedByATestLibrary))
                continue;

            if (type.Name.Contains('<', StringComparison.Ordinal))
                continue;

            // A static class is both abstract and sealed in metadata; it has no instances to
            // seal, so it is not what this rule is about.
            if (type.IsAbstract && type.IsSealed)
                continue;

            if (type.IsSealed)
                yield return type;
        }
    }

    private static bool IsRepositoryContract(Type type)
    {
        if (typeof(IEnumerable).IsAssignableFrom(type) && type != typeof(string))
        {
            foreach (Type argument in type.GetGenericArguments())
            {
                if (IsRepositoryContract(argument))
                    return true;
            }

            return false;
        }

        if (type.IsArray)
            return IsRepositoryContract(type.GetElementType() ?? typeof(object));

        return IsInRepositoriesNamespace(type);
    }

    private static bool IsInRepositoriesNamespace(Type type) =>
        type.Namespace is not null
        && type.Namespace.StartsWith("KyberDash.Contracts.Repositories", StringComparison.Ordinal);

    private static IReadOnlyList<string> ProjectReferenceNames(string csprojPath)
    {
        XDocument document = XDocument.Load(csprojPath);
        List<string> names = [];
        foreach (XElement reference in document.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
        {
            string? include = reference.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(include))
                continue;

            names.Add(Path.GetFileNameWithoutExtension(include.Replace('\\', '/')));
        }

        return names;
    }

    private static Assembly? LoadOptionalAssembly(string simpleName)
    {
        try
        {
            return Assembly.Load(new AssemblyName(simpleName));
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return null;
        }
    }

    private static IEnumerable<string> SourceFiles(string projectDirectory) =>
        Directory.Exists(projectDirectory)
            ? Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            : [];

    /// <summary>
    /// Walks up from the test binary to the repository root and returns its <c>dash/server</c>
    /// directory, or <see langword="null"/> when the binary runs outside the repository tree.
    /// </summary>
    /// <remarks>
    /// The binaries live in <c>dash/server/KyberDash.Api/bin/…</c> and <c>dash/server.tests/…</c>, so
    /// the marker searched for is the directory that holds both <c>dash/server</c> and
    /// <c>dash/server.tests</c>, not a fixed number of parent hops.
    /// </remarks>
    private static string? TryFindPresentationDirectory()
    {
        string? current = AppContext.BaseDirectory;
        while (current is not null)
        {
            if (IsRepositoryRoot(current))
                return Path.Combine(current, "dash", "server");

            current = Path.GetDirectoryName(current);
        }

        return null;
    }

    private static bool IsRepositoryRoot(string directory) =>
        File.Exists(Path.Combine(directory, "dash", "server", "KyberDash.Api", "KyberDash.Api.csproj"))
        && Directory.Exists(Path.Combine(directory, "dash", "server.tests"));

    private static bool IsGeneratedByATestLibrary(Attribute attribute)
    {
        string? ns = attribute.GetType().Namespace;
        return ns is not null
            && (ns.StartsWith("Moq.", StringComparison.Ordinal)
                || ns.StartsWith("Xunit.", StringComparison.Ordinal)
                || ns.StartsWith("Castle.", StringComparison.Ordinal));
    }
}
