using System.Text.RegularExpressions;
using KyberWeave.Core.Diagnostics;

namespace KyberWeave.Core.Security;

/// <summary>
/// Shared heuristic scanner for instruction prose and optional bundled scripts.
/// Callers supply artifact-specific rule codes via <see cref="InstructionSurfaceRuleCodes"/>.
/// </summary>
public static class InstructionSurfaceScanner
{
    public static IEnumerable<Diagnostic> ScanProse(
        string body,
        string subject,
        string filePath,
        InstructionSurfaceRuleCodes codes,
        string artifactLabel)
    {
        foreach ((string? id, Regex? rx, string? label) in InstructionSurfacePatterns.Injection)
        {
            if (!codes.Injection.TryGetValue(id, out string? code))
                continue;
            if (rx.IsMatch(body))
            {
                yield return new Diagnostic(code, Severity.Critical,
                    $"Instruction body contains a {label}. Review carefully before trusting this {artifactLabel}.",
                    subject, filePath);
            }
        }

        foreach (Match m in InstructionSurfacePatterns.HtmlComment.Matches(body))
        {
            string inner = m.Groups[1].Value;
            if (InstructionSurfacePatterns.HtmlCommentInstructionHints.IsMatch(inner))
            {
                yield return new Diagnostic(codes.HtmlComment, Severity.Critical,
                    "Hidden HTML comment in the body contains instruction-like text. Hidden directives are a classic injection vector.",
                    subject, filePath, $"Comment: \"{Truncate(inner.Trim(), 80)}\"");
            }
        }

        foreach (Match m in InstructionSurfacePatterns.Base64Blob.Matches(body))
        {
            yield return new Diagnostic(codes.Base64Blob, Severity.Warning,
                "Body contains a long base64-like blob. Encoded payloads can hide instructions or data; confirm what it decodes to.",
                subject, filePath, $"Starts: {Truncate(m.Value, 24)}…");
            break;
        }

        foreach ((string? id, Regex? rx, string? label) in InstructionSurfacePatterns.Secrets)
        {
            if (!codes.Secrets.TryGetValue(id, out string? code))
                continue;
            if (rx.IsMatch(body))
            {
                yield return new Diagnostic(code, Severity.Critical,
                    $"{artifactLabel} body contains a {label}.", subject, filePath);
            }
        }
    }

    public static IEnumerable<Diagnostic> ScanScriptText(
        string scriptText,
        string scriptRelativePath,
        string scriptAbsolutePath,
        string subject,
        InstructionSurfaceRuleCodes codes)
    {
        foreach ((string? id, Regex? rx, string? label) in InstructionSurfacePatterns.Scripts)
        {
            if (!codes.Scripts.TryGetValue(id, out string? code))
                continue;
            if (rx.IsMatch(scriptText))
            {
                yield return new Diagnostic(code, Severity.Critical,
                    $"Script '{scriptRelativePath}' contains a {label}.", subject, scriptAbsolutePath);
            }
        }

        foreach ((string? id, Regex? rx, string? label) in InstructionSurfacePatterns.Secrets)
        {
            if (!codes.Secrets.TryGetValue(id, out string? code))
                continue;
            if (rx.IsMatch(scriptText))
            {
                yield return new Diagnostic(code, Severity.Critical,
                    $"Script '{scriptRelativePath}' contains a {label}.", subject, scriptAbsolutePath);
            }
        }
    }

    public static IEnumerable<Diagnostic> ScanProvenance(
        IReadOnlyDictionary<string, string>? metadata,
        string? license,
        string subject,
        string filePath,
        InstructionSurfaceRuleCodes codes)
    {
        if (metadata is null || !HasKey(metadata, "author"))
        {
            yield return new Diagnostic(codes.MissingAuthor, Severity.Info,
                "No 'author' (or metadata.author) for provenance. Enterprise governance benefits from a clear owner.",
                subject, filePath);
        }

        if (metadata is null || !HasKey(metadata, "version"))
        {
            yield return new Diagnostic(codes.MissingVersion, Severity.Info,
                "No 'version' (or metadata.version) for provenance. Versioning makes review and rollback auditable.",
                subject, filePath);
        }

        if (string.IsNullOrWhiteSpace(license) && (metadata is null || !HasKey(metadata, "license")))
        {
            yield return new Diagnostic(codes.MissingLicense, Severity.Info,
                "No 'license' declared. Declare a license, especially for shared or community artifacts.",
                subject, filePath);
        }
    }

    private static bool HasKey(IReadOnlyDictionary<string, string> metadata, string key) =>
        metadata.Keys.Any(k => k.Equals(key, StringComparison.OrdinalIgnoreCase))
        || metadata.Keys.Any(k => k.Equals($"metadata.{key}", StringComparison.OrdinalIgnoreCase));

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];
}
