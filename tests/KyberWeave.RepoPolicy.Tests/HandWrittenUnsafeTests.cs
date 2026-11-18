using System.Text;
using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>Contract for task fix1, F3: the Core and Data sources hold no hand-written <c>unsafe</c> outside generated import stubs.</summary>
public sealed class HandWrittenUnsafeTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    // F3: no hand-written `unsafe` in Core or Data outside generated LibraryImport stubs.
    [Fact]
    public void Fix3_CoreHasNoHandWrittenUnsafe()
    {
        string[] roots = ["src/KyberWeave.Core", "src/KyberWeave.Data"];
        List<string> offenders = [];
        foreach (string file in roots.SelectMany(root => Corpus.EnumerateFiles(root, "*.cs")))
        {
            foreach (string line in StripCommentsAndStrings(Corpus.ReadText(file)).Split('\n'))
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(line, @"\bunsafe\b"))
                {
                    offenders.Add($"{file}: {line.Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "Hand-written unsafe outside generated stubs:\n" + string.Join("\n", offenders));
    }

    private static string StripCommentsAndStrings(string text)
    {
        StringBuilder kept = new(text.Length);
        int index = 0;
        bool inLineComment = false;
        bool inBlockComment = false;
        bool inString = false;
        bool inChar = false;
        bool verbatim = false;
        while (index < text.Length)
        {
            char current = text[index];
            char next = index + 1 < text.Length ? text[index + 1] : '\0';
            if (inLineComment)
            {
                if (current == '\n')
                {
                    inLineComment = false;
                    kept.Append('\n');
                }

                index++;
                continue;
            }

            if (inBlockComment)
            {
                if (current == '*' && next == '/')
                {
                    inBlockComment = false;
                    index += 2;
                }
                else
                {
                    if (current == '\n')
                    {
                        kept.Append('\n');
                    }

                    index++;
                }

                continue;
            }

            if (inString)
            {
                if (!verbatim && current == '\\')
                {
                    index += 2;
                    continue;
                }

                if (current == '"')
                {
                    if (verbatim && next == '"')
                    {
                        index += 2;
                        continue;
                    }

                    inString = false;
                }

                index++;
                continue;
            }

            if (inChar)
            {
                if (current == '\\')
                {
                    index += 2;
                    continue;
                }

                if (current == '\'')
                {
                    inChar = false;
                }

                index++;
                continue;
            }

            if (current == '/' && next == '/')
            {
                inLineComment = true;
                index += 2;
                continue;
            }

            if (current == '/' && next == '*')
            {
                inBlockComment = true;
                index += 2;
                continue;
            }

            if (current == '@' && next == '"')
            {
                inString = true;
                verbatim = true;
                index += 2;
                continue;
            }

            if (current == '$' && next == '"')
            {
                inString = true;
                verbatim = false;
                index += 2;
                continue;
            }

            if (current == '"')
            {
                inString = true;
                verbatim = false;
                index++;
                continue;
            }

            if (current == '\'')
            {
                inChar = true;
                index++;
                continue;
            }

            kept.Append(current);
            index++;
        }

        return kept.ToString();
    }
}
