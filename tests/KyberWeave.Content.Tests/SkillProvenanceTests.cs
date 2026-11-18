using KyberWeave.Application.Skills;
using KyberWeave.Data.Hooks;
using KyberWeave.Data.Host;
using KyberWeave.Data.Paths;
using KyberWeave.Tests.Corpus;
using KyberWeave.Tests.Skills;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Every skill under <c>.apm/skills</c> must declare provenance in the nested
/// <c>metadata:</c> map the gate reads. A top-level <c>author:</c>/<c>version:</c> satisfies
/// nothing and adds <c>KW-SKILL-SPEC-009</c>, so this parses with the real engine and scans
/// with the real scanner instead of grepping the text. The check is repository-wide because
/// the fix was reverted once; a single-skill assertion would not notice.
/// </summary>
public sealed class SkillProvenanceTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    [Fact]
    public void EveryApmSkillDeclaresNestedProvenanceMetadata()
    {
        PhysicalFileSystem files = new();
        SkillFileReader reader = new(files, new PhysicalPathResolver(files), new ProcessEnvironment());
        string skillsRoot = Path.Combine(Corpus.Root, ".apm", "skills");
        IEnumerable<string> skillFiles = Corpus
            .EnumerateFiles(".apm/skills", "SKILL.md", recursive: true)
            .Select(path => Path.Combine(Corpus.Root, path.Replace('/', Path.DirectorySeparatorChar)));

        Assert.Empty(SkillProvenanceScan.Issues(skillsRoot, skillFiles, reader.ReadFile, files.ReadAllText));
    }
}
