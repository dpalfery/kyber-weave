using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Graph;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Tests.Corpus;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     Guards the governed docs corpus against broken Markdown link fragments (<c>KW-DOC-LINK-001</c>).
///     T10 repaired six previously broken anchors; this test fails if those fragments return or any
///     other <c>#fragment</c> does not match a heading in its target document.
/// </summary>
public sealed class DocLinkFragmentValidationTests
{
    private const string HostConfig = ".kyber-weave/kyber-weave.yml";

    private const string DocsRoot = "docs";

    [Fact]
    public void KnownBrokenCorpusFragmentsReportKWDOCLINK001()
    {
        ICorpusSource corpus = CheckoutCorpusSource.Checkout;
        MemoryDocsHost host = new();
        host.WriteBytesInRepo(HostConfig, corpus.ReadBytes(HostConfig));
        foreach (string path in corpus.EnumerateFiles(DocsRoot))
        {
            host.WriteBytesInRepo(path, corpus.ReadBytes(path));
        }

        KyberWeaveConfig config = host.Services().GetRequiredService<IKyberWeaveConfigRepository>().Load(host.Repo);
        DocumentSet set = new DocumentLoader(host.Store, host.Repo, host.Paths, host.Environment, config.Ontology).Load();
        DiagnosticReport report = new DocSpecValidator(host.Repo, config.Ontology, host.Store, host.Paths, host.Environment).Validate(set);

        List<LinkFragmentFinding> findings = report.Items
            .Where(item => item.Code == DocSpecValidator.BrokenLinkFragment)
            .Select(item => ToFinding(set, item))
            .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
            .ThenBy(f => f.Line)
            .ThenBy(f => f.Fragment, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            findings.Count == 0,
            "Expected no KW-DOC-LINK-001 findings in the governed docs corpus. Broken fragments:"
            + Environment.NewLine
            + string.Join(
                Environment.NewLine,
                findings.Select(f => $"{f.SourcePath}:{f.Line} #{f.Fragment} -> {f.TargetPath}")));
    }

    private static LinkFragmentFinding ToFinding(DocumentSet set, Diagnostic diagnostic)
    {
        DocumentModel source = set.Documents.Single(doc => doc.RelativePath == diagnostic.FilePath);
        int bodyLine = diagnostic.StartLine!.Value - source.BodyStartLine + 1;
        BodyLink link = source.LinkTargets.First(bodyLink =>
            bodyLink.Line == bodyLine
            && diagnostic.Message.StartsWith(
                $"Link fragment '#{bodyLink.Fragment}' does not match",
                StringComparison.Ordinal));

        string targetPath = string.IsNullOrEmpty(link.Path)
            ? source.RelativePath
            : DocumentLinkPaths.Resolve(source.RelativePath, link.Path)
              ?? throw new InvalidOperationException(
                  $"Could not resolve link target from {source.RelativePath}.");

        return new LinkFragmentFinding(
            source.RelativePath,
            diagnostic.StartLine!.Value,
            link.Fragment,
            targetPath);
    }

    private sealed record LinkFragmentFinding(
        string SourcePath,
        int Line,
        string Fragment,
        string TargetPath);
}
