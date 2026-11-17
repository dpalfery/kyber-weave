using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Assertions over text a command printed to the console. A console folds at its render width, and a
/// sentence carrying an absolute path can therefore break across two lines; these assertions compare
/// words, so a fact pins the sentence rather than where the console happened to fold it.
/// </summary>
internal static class ConsoleOutputAssert
{
    /// <summary>Asserts <paramref name="output"/> carries <paramref name="expected"/> once every run of whitespace is one space.</summary>
    public static void ContainsUnwrapped(string expected, string output)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(output);

        Assert.True(
            Unwrap(output).Contains(expected, StringComparison.Ordinal),
            $"Expected '{expected}' in the output, ignoring line breaks. Output:{Environment.NewLine}{output}");
    }

    /// <summary><paramref name="text"/> with every run of whitespace collapsed to one space.</summary>
    public static string Unwrap(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
