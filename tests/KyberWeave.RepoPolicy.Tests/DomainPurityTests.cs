using System.Text.RegularExpressions;
using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Static audit that the Domain layer stays pure: no ambient file system, process, environment,
/// console or wall-clock access. Domain logic takes the facts it needs as parameters, the ports
/// that gather them live in Contracts, their adapters live in Data, and Application wires them.
/// Kernel is the bottom layer and the deliberate home of the file and process primitives, so it
/// is not scanned here.
/// This test only reads source. It touches no product code.
/// </summary>
public sealed class DomainPurityTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    // The prefix excludes a member access such as `error.File.Length`, which is data, not I/O.
    private static readonly Regex AmbientIo = new(
        @"(?<![\w.])File\.|(?<![\w.])Directory\.|(?<![\w.])Environment\.|(?<![\w.])Process\.|DateTime\.UtcNow|(?<![\w.])Console\.",
        RegexOptions.CultureInvariant);

    // Folders still held by the other wave-4 packages, which move their I/O out under their own
    // tasks. Shrink this list as those land; never grow it.
    private static readonly string[] Pending =
    [
        "src/KyberWeave.Domain/Docs/",   // wave4-DC: the MCP wiring receipt and the spec validator
        "src/KyberWeave.Domain/Squad/"   // wave4-SQ: the physical-root port
    ];

    [Fact]
    public void DomainUsesNoAmbientIoOutsideTheFoldersTheOtherWaveFourPackagesOwn()
    {
        List<string> violations = [];

        foreach (string relative in Corpus.EnumerateFiles("src/KyberWeave.Domain", "*.cs"))
        {
            // The corpus already leaves build output out of a recursive listing; this second check keeps the audit
            // correct if that listing rule ever changes.
            if (relative.Contains("/bin/", StringComparison.Ordinal) ||
                relative.Contains("/obj/", StringComparison.Ordinal))
            {
                continue;
            }

            if (Pending.Any(prefix => relative.StartsWith(prefix, StringComparison.Ordinal)))
                continue;

            string[] lines = Corpus.ReadText(relative).ReplaceLineEndings("\n").Split('\n');
            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index];
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                    continue;

                if (AmbientIo.IsMatch(line))
                    violations.Add($"{relative}:{index + 1}: {line.Trim()}");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Domain code must not touch ambient I/O; inject the fact or move the call behind a "
            + "Contracts port with a Data adapter:\n" + string.Join("\n", violations));
    }
}
