using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>Chooses the provider and stores the TypeSafe key for the endpoint origin.</summary>
/// <remarks>
/// Writes the chosen provider (<c>none</c>, TypeSafe cloud or local Ollama) to the
/// user override at <c>~/.config/kyber-weave/arbiter.yml</c>, which may hold
/// <c>provider:</c> and nothing else. A TypeSafe key is read from
/// <c>--key-stdin</c> or a masked prompt and stored for the endpoint origin; it is
/// never echoed, never written to the override, and never appears in argv. An
/// Ollama 0.35 or later daemon found at <c>GET {base}/api/version</c> suggests the
/// <c>nimble</c> model, and a <c>tev1</c> model warns that its usable input of
/// about 2,000 tokens is too small for most shipped rules.
/// </remarks>
public sealed class ArbiterSetupCommand : Command<ArbiterSettings>
{
    /// <summary>The TypeSafe cloud endpoint written when no <c>--endpoint</c> is given.</summary>
    internal const string TypeSafeEndpoint = "https://api.typesafe.ai/v1";

    /// <summary>The local Ollama endpoint a loopback choice points at.</summary>
    internal const string OllamaEndpoint = "http://localhost:11434/v1";

    /// <summary>The Ollama base URL probed when no loopback endpoint or <c>--ollama</c> names one.</summary>
    internal const string OllamaBaseUrl = "http://localhost:11434";

    /// <summary>
    /// Writes the provider choice to the user override, stores a remote key, and
    /// reports Ollama detection. Exits 1 on an unsupported <c>--provider</c>, an
    /// unusable <c>--endpoint</c>, an unwritable override, or a failed key store
    /// write (with a hint to use <c>TYPESAFE_API_KEY</c>); detection failures only
    /// silence the suggestion, never the run.
    /// </summary>
    protected override int Execute(CommandContext context, ArbiterSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        DiagnosticReport report = new();

        if (!TryParseProvider(settings.Provider, out ArbiterProviderKind kind, out Diagnostic? providerError))
        {
            report.Add(providerError!);
            CommandHelpers.Finish(report, settings, "arbiter setup", "Rule");
            return 1;
        }

        string endpoint = string.IsNullOrWhiteSpace(settings.Endpoint)
            ? TypeSafeEndpoint
            : settings.Endpoint.Trim();
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? endpointUri)
            || (endpointUri.Scheme != Uri.UriSchemeHttp && endpointUri.Scheme != Uri.UriSchemeHttps))
        {
            report.Add(new Diagnostic(
                RuleValidator.MalformedSection,
                Severity.Error,
                $"--endpoint '{endpoint}' is not an absolute http(s) URL.",
                "--endpoint",
                Hint: "Use https://api.typesafe.ai/v1, or an http(s) Ollama URL."));
            CommandHelpers.Finish(report, settings, "arbiter setup", "Rule");
            return 1;
        }

        string model = string.IsNullOrWhiteSpace(settings.Model)
            ? (ArbiterKeyResolver.IsLoopbackEndpoint(endpoint) ? "nimble" : "jev-1.13.0")
            : settings.Model.Trim();

        string kindName = kind == ArbiterProviderKind.Systemone ? "systemone" : "none";
        string overridePath = ArbiterUserSettings.GetPath(ArbiterCommandComposition.HomeDirectory());
        try
        {
            string? directory = Path.GetDirectoryName(overridePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(overridePath, RenderOverride(kindName, endpoint, model));
        }
        catch (ArgumentException exception)
        {
            report.Add(new Diagnostic(
                RuleValidator.MalformedSection,
                Severity.Error,
                $"--model cannot be written to the user override: {exception.Message.Split('\n')[0]}",
                "--model",
                Hint: "Use a model name with no newline or control characters, for example jev-1.13.0 or nimble."));
            CommandHelpers.Finish(report, settings, "arbiter setup", "Rule");
            return 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            report.Add(new Diagnostic(
                RuleValidator.MalformedSection,
                Severity.Error,
                $"The user override '{overridePath}' could not be written: {exception.Message}",
                "arbiter",
                overridePath,
                Hint: "Confirm the home directory is writable, or set provider fields in kyber-weave.yml."));
            CommandHelpers.Finish(report, settings, "arbiter setup", "Rule");
            return 1;
        }

        AnsiConsole.MarkupLine("[bold]Arbiter setup[/]");
        AnsiConsole.MarkupLine($"  Provider: [bold]{Markup.Escape(kindName)}[/]");
        AnsiConsole.MarkupLine($"  Endpoint: [bold]{Markup.Escape(endpoint)}[/]");
        AnsiConsole.MarkupLine($"  Model: [bold]{Markup.Escape(model)}[/]");
        AnsiConsole.MarkupLine($"  Override: [bold]{Markup.Escape(overridePath)}[/]");

        ReportOllama(settings, endpoint, model);

        if (kind == ArbiterProviderKind.Systemone && !ArbiterKeyResolver.IsLoopbackEndpoint(endpoint)
            && !StoreKey(settings, endpoint, report))
        {
            CommandHelpers.Finish(report, settings, "arbiter setup", "Rule");
            return 1;
        }

        CommandHelpers.Finish(report, settings, "arbiter setup", "Rule");
        return report.HasErrors ? 1 : 0;
    }

    /// <summary>Runs the command without cancellation; the entry point tests use.</summary>
    public int Execute(CommandContext context, ArbiterSettings settings) =>
        Execute(context, settings, CancellationToken.None);

    private static void ReportOllama(ArbiterSettings settings, string endpoint, string model)
    {
        string probeBase = !string.IsNullOrWhiteSpace(settings.Ollama)
            ? settings.Ollama.Trim()
            : ArbiterKeyResolver.IsLoopbackEndpoint(endpoint)
                ? ArbiterKeyResolver.GetOrigin(endpoint) ?? OllamaBaseUrl
                : OllamaBaseUrl;

        Uri? versionUri = BuildOllamaVersionUri(probeBase);
        if (versionUri is not null
            && ArbiterCommandComposition.TryDetectOllama(versionUri, out string? version))
        {
            AnsiConsole.MarkupLine(
                $"  [green]ok[/] Ollama {Markup.Escape(version ?? "0.35")} detected at {Markup.Escape(probeBase)}; suggested model: [bold]nimble[/].");
        }

        if (model.StartsWith("tev1", StringComparison.Ordinal))
        {
            AnsiConsole.MarkupLine(
                "  [yellow]warn[/] Model 'tev1' has a usable input of about 2,000 tokens, too small for most shipped rules; prefer nimble or jev-1.13.0.");
        }
    }

    private static Uri? BuildOllamaVersionUri(string probeBase) =>
        Uri.TryCreate(probeBase.TrimEnd('/') + "/api/version", UriKind.Absolute, out Uri? uri)
            ? uri
            : null;

    private static bool StoreKey(ArbiterSettings settings, string endpoint, DiagnosticReport report)
    {
        // The key travels on stdin or through a masked prompt, and is stored for the
        // endpoint origin only. It is never printed, so diagnostics and summaries name
        // the origin while the value stays out of every surface.
        string? key = settings.KeyStdin
            ? ArbiterCommandComposition.KeyStdinReader()
            : ArbiterCommandComposition.KeyPrompt();
        if (string.IsNullOrEmpty(key))
        {
            AnsiConsole.MarkupLine(
                "  [yellow]warn[/] No key supplied; a remote provider reports KW-ARB-KEY-001 until one resolves.");
            return true;
        }

        string? origin = ArbiterKeyResolver.GetOrigin(endpoint);
        if (origin is null)
            return true;

        try
        {
            ArbiterCommandComposition.CredentialStore().Write(origin, key);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            report.Add(new Diagnostic(
                ArbiterStatusCommand.KeyMissing,
                Severity.Error,
                $"The credential store for origin '{origin}' is not available: {exception.Message}",
                "arbiter.provider",
                Hint: $"Store the key in {ArbiterKeyResolver.EnvVarName} instead."));
            return false;
        }

        AnsiConsole.MarkupLine($"  [green]ok[/] Stored a key for {Markup.Escape(origin)}.");
        return true;
    }

    /// <summary>
    /// Renders the user override. Every value is written as a single-quoted YAML scalar
    /// and every control character is rejected, so a value carrying YAML syntax is read
    /// back as the value rather than as structure.
    /// </summary>
    /// <remarks>
    /// The model, and the endpoint, come from the command line. Interpolated raw, a value
    /// containing a colon, a quote or a <c>#</c> changed what the next read saw, and a
    /// newline could append arbitrary keys to the operator's override. Single quotes are
    /// the only YAML quoting with no escape sequences: the one character that cannot
    /// appear inside them is the single quote itself, which is written doubled. A newline
    /// is rejected rather than escaped, because no quoting carries one on a single line.
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown when a value contains a control character.</exception>
    private static string RenderOverride(string kindName, string endpoint, string model) =>
        $"provider:\n  kind: {YamlScalar(kindName)}\n  endpoint: {YamlScalar(endpoint)}\n  model: {YamlScalar(model)}\n";

    private static string YamlScalar(string value)
    {
        if (value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "An arbiter setup value cannot contain newline or other control characters: " +
                "it would not survive the YAML round trip.",
                nameof(value));
        }

        return $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
    }

    private static bool TryParseProvider(string? raw, out ArbiterProviderKind kind, out Diagnostic? error)
    {
        kind = ArbiterProviderKind.None;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
            return true;

        if (string.Equals(raw.Trim(), "none", StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(raw.Trim(), "systemone", StringComparison.OrdinalIgnoreCase))
        {
            kind = ArbiterProviderKind.Systemone;
            return true;
        }

        error = new Diagnostic(
            RuleValidator.MalformedSection,
            Severity.Error,
            $"--provider '{raw}' is not supported.",
            "--provider",
            Hint: "Use none or systemone.");
        return false;
    }
}
