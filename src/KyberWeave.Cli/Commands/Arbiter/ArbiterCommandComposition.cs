using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Processes;
using Spectre.Console;
using YamlDotNet.Core;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>
/// CLI composition root for the read-only Arbiter surfaces: supplies the HTTP
/// handler, credential store, home path, process runner and clock that Core
/// takes as constructor arguments but never constructs itself.
/// </summary>
public static class ArbiterCommandComposition
{
    /// <summary>The operator home directory the user override resolves under.</summary>
    public static Func<string> HomeDirectory { get; set; } =
        () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The clock evaluations stamp records with.</summary>
    public static Func<TimeProvider> Clock { get; set; } = () => TimeProvider.System;

    /// <summary>The HTTP transport step-1 calls run on.</summary>
    public static Func<HttpMessageHandler> HttpHandler { get; set; } = () => new HttpClientHandler();

    /// <summary>The child-process seam credential stores shell out through.</summary>
    public static Func<ICredentialProcessRunner> CredentialProcessRunner { get; set; } =
        () => new ProcessRunnerCredentialProcessRunner();

    /// <summary>
    /// The child-process seam git probes run through. Arguments travel in
    /// <see cref="ProcessStartInfo.ArgumentList"/>, never a shell string.
    /// </summary>
    public static Func<ProcessStartInfo, string, ProcessResult> RunProcess { get; set; } =
        (startInfo, standardInput) => ProcessRunner.Run(startInfo, standardInput);

    /// <summary>Creates the OS credential store for the Arbiter key.</summary>
    public static ICredentialStore CreateCredentialStore()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsCredentialStore();
        if (OperatingSystem.IsMacOS())
            return new MacKeychainCredentialStore(CredentialProcessRunner());
        return new SecretServiceCredentialStore(CredentialProcessRunner());
    }

    /// <summary>The credential store the key is read from and written to.</summary>
    /// <remarks>
    /// Settable so <c>setup</c>, <c>status</c> and <c>doctor</c> tests inject a fake
    /// store and assert the exact origin without touching the OS keychain.
    /// </remarks>
    public static Func<ICredentialStore> CredentialStore { get; set; } = () => CreateCredentialStore();

    /// <summary>Reads a process environment variable; injected so tests stay hermetic.</summary>
    public static Func<string, string?> GetEnvironmentVariable { get; set; } =
        name => Environment.GetEnvironmentVariable(name);

    /// <summary>Reads the TypeSafe key from stdin for <c>setup --key-stdin</c>.</summary>
    public static Func<string?> KeyStdinReader { get; set; } = () => Console.In.ReadLine();

    /// <summary>Prompts for the TypeSafe key with input hidden; the key is never echoed.</summary>
    public static Func<string?> KeyPrompt { get; set; } = () => PromptKeyMasked();

    /// <summary>The process boundary the Arbiter binary probe runs through.</summary>
    /// <remarks>
    /// <c>doctor</c> probes <c>kyber-weave-arbiter --version</c> through
    /// <see cref="ArbiterProcessProbe"/> from the Squad surface; tests inject a stub
    /// executor so the probe never leaves the process.
    /// </remarks>
    public static Func<IProcessExecutor> ArbiterProbeExecutor { get; set; } = () => ProcessExecutor.Instance;

    /// <summary>
    /// Resolves the key for <paramref name="endpoint"/> through the injected store and
    /// environment, binding the environment key to the TypeSafe origin or the user
    /// override's endpoint. A loopback endpoint needs no key and returns null.
    /// </summary>
    public static string? ResolveKey(KyberWeaveConfig config, string endpoint)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        return ArbiterKeyResolver.Resolve(
            endpoint, CredentialStore(), GetEnvironmentVariable, UserOverrideEndpoint(config));
    }

    /// <summary>
    /// Resolves the key, treating a failing store as an absent key. A host without a
    /// usable credential store reports <c>KW-ARB-KEY-001</c> rather than crashing
    /// <c>status</c> and <c>doctor</c>; <c>setup</c> keeps the failure so it can hint
    /// at <c>TYPESAFE_API_KEY</c> instead.
    /// </summary>
    public static string? TryResolveKey(KyberWeaveConfig config, string endpoint)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        try
        {
            return ResolveKey(config, endpoint);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Probes an Ollama <c>/api/version</c> URL for a 0.35 or later daemon, which
    /// answers <c>{"version":"x.y.z"}</c>.
    /// </summary>
    /// <remarks>
    /// Any transport, parse or version failure means "not found", never an error:
    /// detection only suggests a model, so an absent daemon must not fail setup.
    /// </remarks>
    /// <returns>True with the daemon version when 0.35 or later answered.</returns>
    public static bool TryDetectOllama(Uri versionUri, out string? version)
    {
        version = null;
        ArgumentNullException.ThrowIfNull(versionUri);
        if (versionUri.Scheme != Uri.UriSchemeHttp && versionUri.Scheme != Uri.UriSchemeHttps)
            return false;

        try
        {
            using HttpClient client = new(HttpHandler(), disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(5),
            };
            using HttpResponseMessage response = client.GetAsync(versionUri).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                return false;

            string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using JsonDocument document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("version", out JsonElement versionElement)
                || versionElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            string? found = versionElement.GetString();
            if (!IsOllamaSupported(found))
                return false;

            version = found;
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException
            or TaskCanceledException
            or TimeoutException
            or JsonException
            or InvalidOperationException
            or ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>Whether an Ollama <c>/api/version</c> version is 0.35 or later.</summary>
    internal static bool IsOllamaSupported(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return false;

        Match match = Regex.Match(version.Trim(), @"^(?<major>\d+)\.(?<minor>\d+)");
        if (!match.Success)
            return false;

        if (!int.TryParse(
                match.Groups["major"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int major)
            || !int.TryParse(
                match.Groups["minor"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int minor))
        {
            return false;
        }

        return major > 0 || (major == 0 && minor >= 35);
    }

    private static string? PromptKeyMasked()
    {
        try
        {
            return AnsiConsole.Prompt(
                new TextPrompt<string>("TypeSafe API key (input hidden, never echoed):").Secret());
        }
        catch (InvalidOperationException)
        {
            // Non-interactive console: there is no key to read.
            return null;
        }
    }

    /// <summary>
    /// Loads the host configuration and applies the user override, reporting
    /// failures as diagnostics. A load that already names a
    /// <c>KW-ARB-CONFIG-*</c> code keeps it; any other load failure surfaces as
    /// <c>KW-CONFIG-001</c>, matching <see cref="CommandHelpers.TryLoadConfig"/>.
    /// </summary>
    public static bool TryResolveConfig(
        ArbiterSettings settings,
        DiagnosticReport report,
        out KyberWeaveConfig config,
        out string? configPath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(report);

        KyberWeaveConfigLoadResult loaded = KyberWeaveConfigLoader.TryLoad(settings.Path, settings.Config);
        if (!loaded.Success)
        {
            report.Add(MapLoadFailure(loaded.Error ?? "Failed to load kyber-weave.yml.", loaded.ConfigPath));
            config = KyberWeaveConfig.ProductDefaults;
            configPath = loaded.ConfigPath;
            return false;
        }

        config = loaded.Config!;
        configPath = loaded.ConfigPath;

        try
        {
            ArbiterProviderConfig repository = config.Arbiter.Provider;
            ArbiterProviderConfig merged = ArbiterUserSettings.ApplyTo(repository, HomeDirectory());
            if (!merged.Equals(repository))
            {
                config = new KyberWeaveConfig
                {
                    Ontology = config.Ontology,
                    Harness = config.Harness,
                    DocsAnalysis = config.DocsAnalysis,
                    Squad = config.Squad,
                    ConfigReg = config.ConfigReg,
                    Review = config.Review,
                    Arbiter = config.Arbiter with { Provider = merged },
                };
            }
        }
        catch (YamlException exception)
        {
            report.Add(MapLoadFailure(
                exception.Message,
                ArbiterUserSettings.GetPath(HomeDirectory())));
            return false;
        }

        return true;
    }

    /// <summary>
    /// Resolves the repository root for a working path: <c>git rev-parse
    /// --show-toplevel</c> first, the path itself when git cannot answer (outside
    /// a repository, or without git on <c>PATH</c>), so offline reads degrade
    /// rather than fail.
    /// </summary>
    public static string ResolveRepositoryRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string working = Path.GetFullPath(path);
        try
        {
            ProcessStartInfo startInfo = new("git")
            {
                WorkingDirectory = Directory.Exists(working) ? working : Path.GetDirectoryName(working) ?? ".",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("rev-parse");
            startInfo.ArgumentList.Add("--show-toplevel");
            ProcessResult result = RunProcess(startInfo, string.Empty);
            string root = result.ExitCode == 0 ? result.StandardOutput.Trim() : string.Empty;
            if (!string.IsNullOrWhiteSpace(root))
                return root;
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            // No git, no root: the caller works from the path itself.
        }

        return working;
    }

    /// <summary>
    /// Resolves the arbiter directory holding the ledger and the decision log:
    /// <c>artifacts/arbiter/</c> under the repository root.
    /// </summary>
    public static string ResolveArbiterDirectory(string path) =>
        Path.Combine(ResolveRepositoryRoot(path), "artifacts", "arbiter");

    /// <summary>
    /// Creates the step-1 provider for <c>eval</c>: the configured provider, or
    /// the <c>--provider</c> override when one is supplied. Returns null and
    /// reports when the override or endpoint is unusable. Ownership of the
    /// returned provider transfers to the caller, which disposes it via the
    /// evaluator session.
    /// </summary>
    public static IArbiterProvider? CreateProvider(
        KyberWeaveConfig config,
        string? providerOverride,
        DiagnosticReport report,
        out Func<Uri, string?> keyResolver)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(report);

        ICredentialStore store = CreateCredentialStore();
        string? userOverrideEndpoint = UserOverrideEndpoint(config);
        keyResolver = uri => ArbiterKeyResolver.Resolve(uri.ToString(), store, userOverrideEndpoint);

        ArbiterProviderKind kind = config.Arbiter.Provider.Kind;
        if (providerOverride is not null)
        {
            if (string.Equals(providerOverride, "none", StringComparison.OrdinalIgnoreCase))
            {
                kind = ArbiterProviderKind.None;
            }
            else if (string.Equals(providerOverride, "systemone", StringComparison.OrdinalIgnoreCase))
            {
                kind = ArbiterProviderKind.Systemone;
            }
            else
            {
                report.Add(new Diagnostic(
                    RuleValidator.MalformedSection,
                    Severity.Error,
                    $"--provider '{providerOverride}' is not supported.",
                    "--provider",
                    Hint: "Use none or systemone."));
                return null;
            }
        }

        if (kind == ArbiterProviderKind.None)
        {
            return new NoneProvider();
        }

        if (!Uri.TryCreate(config.Arbiter.Provider.Endpoint, UriKind.Absolute, out Uri? endpoint))
        {
            report.Add(new Diagnostic(
                RuleValidator.MalformedSection,
                Severity.Error,
                $"arbiter.provider.endpoint '{config.Arbiter.Provider.Endpoint}' is not an absolute URL.",
                "arbiter.provider",
                Hint: "Use https://api.typesafe.ai/v1, or an http(s) Ollama URL."));
            return null;
        }

        Func<Uri, string?> captured = keyResolver;
        return new SystemOneClient(
            HttpHandler(),
            endpoint,
            config.Arbiter.Provider.Model,
            config.Arbiter.Provider.TimeoutMs,
            captured);
    }

    /// <summary>
    /// Creates the dry-run evaluator for <c>eval</c> over already-constructed
    /// collaborators. Returns null and reports when the provider cannot be
    /// built. Ownership of the returned session transfers to the caller, which
    /// disposes it after the evaluation.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The provider's lifetime transfers to the returned session, which the eval command disposes.")]
    public static ArbiterEvaluatorSession? CreateEvaluator(
        KyberWeaveConfig config,
        string repositoryRoot,
        string? providerOverride,
        DiagnosticReport report)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(report);

        IArbiterProvider? provider = CreateProvider(config, providerOverride, report, out Func<Uri, string?> keyResolver);
        if (provider is null)
            return null;

        string arbiterDirectory = Path.Combine(repositoryRoot, "artifacts", "arbiter");
        ArbiterEvaluator evaluator = new(
            provider,
            keyResolver,
            new InFlightLedger(arbiterDirectory),
            new DecisionLog(arbiterDirectory),
            new ArbiterCliGitFacts(),
            new ArbiterCliPlanReader(repositoryRoot),
            Clock());
        return new ArbiterEvaluatorSession(evaluator, provider as IDisposable);
    }

    private static string? UserOverrideEndpoint(KyberWeaveConfig config)
    {
        try
        {
            ArbiterProviderConfig repository = config.Arbiter.Provider;
            ArbiterProviderConfig merged = ArbiterUserSettings.ApplyTo(repository, HomeDirectory());
            return string.Equals(merged.Endpoint, repository.Endpoint, StringComparison.Ordinal)
                ? null
                : merged.Endpoint;
        }
        catch (YamlException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Diagnostic MapLoadFailure(string message, string? filePath)
    {
        Match match = Regex.Match(message, @"KW-ARB-CONFIG-\d{3}");
        string code = match.Success
            ? match.Value
            : KyberWeaveConfigLoader.ConfigLoadErrorCode;
        return new Diagnostic(
            code,
            Severity.Error,
            message,
            "arbiter",
            filePath,
            Hint: "Fix the arbiter: section, or remove it to take the product defaults.");
    }

    /// <summary>
    /// The git facts for an offline dry run: the base bag already carries every
    /// fact the trigger supplies (absent as null), and with no live repository
    /// state there is nothing truthful to add, so the bag passes through
    /// unchanged and absent facts keep satisfying <c>exists: false</c>.
    /// </summary>
    private sealed class ArbiterCliGitFacts : IArbiterGitFacts
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification, ArbiterEvent ev) =>
            facts;
    }

    /// <summary>
    /// The plan facts for a dry run, read from the plan file the dispatch
    /// headers name. Plan and task identity come from <c>PLAN_FILE</c> and
    /// <c>TASK</c> only; nothing is inferred from the plan index. The header is
    /// caller-controlled, so a path that does not resolve inside the repository
    /// reads as <c>plan.exists: false</c>.
    /// </summary>
    /// <remarks>
    /// Internal rather than private so the tests reach this reader itself. The
    /// hook host's equivalent is internal for the same reason, and a containment
    /// rule proved against a stand-in reader is not a proof about the reader that
    /// ships.
    /// </remarks>
    internal sealed class ArbiterCliPlanReader(string repositoryRoot) : IArbiterPlanReader
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification)
        {
            ArgumentNullException.ThrowIfNull(facts);
            ArgumentNullException.ThrowIfNull(classification);

            if (!classification.Headers.TryGetValue("PLAN_FILE", out string? planFile)
                || string.IsNullOrWhiteSpace(planFile))
            {
                return facts;
            }

            string? resolved = PlanPathResolver.Resolve(planFile, repositoryRoot);
            if (resolved is null || !File.Exists(resolved))
            {
                return facts.With("plan.exists", false, ArbiterFactLabel.Derived);
            }

            PlanDocument document;
            string content;
            try
            {
                content = File.ReadAllText(resolved);
                document = PlanDocumentParser.Parse(content);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return facts.With("plan.exists", false, ArbiterFactLabel.Derived);
            }

            // The digest of the text just parsed; the evaluator keys REPEAT on it.
            facts = facts.With("plan.digest", PlanDocumentParser.Digest(content), ArbiterFactLabel.Derived);
            facts = facts.With("plan.exists", true, ArbiterFactLabel.Derived);
            if (document.Status is not null)
                facts = facts.With("plan.status", document.Status, ArbiterFactLabel.Derived);
            facts = facts.With("plan.development-mode", document.DevelopmentMode, ArbiterFactLabel.Derived);
            facts = facts.With("plan.tasks.count", document.Tasks.Count, ArbiterFactLabel.Derived);
            facts = facts.With("plan.out-of-scope", document.OutOfScope.ToList(), ArbiterFactLabel.Derived);

            if (classification.Headers.TryGetValue("TASK", out string? taskId)
                && !string.IsNullOrWhiteSpace(taskId))
            {
                PlanTask? task = document.Tasks.FirstOrDefault(
                    candidate => string.Equals(candidate.Id, taskId, StringComparison.Ordinal));
                if (task is not null)
                {
                    facts = facts.With("plan.task", task.Id, ArbiterFactLabel.Derived);
                    facts = facts.With("plan.task.text", task.Text, ArbiterFactLabel.Derived);
                    facts = facts.With("plan.task.files", task.Files.ToList(), ArbiterFactLabel.Derived);
                    facts = facts.With("plan.task.depends-on", task.DependsOn.ToList(), ArbiterFactLabel.Derived);
                    facts = facts.With("plan.task.skills", task.Skills.ToList(), ArbiterFactLabel.Derived);
                    facts = facts.With(
                        "plan.test-contract.row",
                        document.ContractRows.Any(row => string.Equals(row.TaskId, task.Id, StringComparison.Ordinal)),
                        ArbiterFactLabel.Derived);
                }
            }

            return facts;
        }
    }
}

/// <summary>
/// One dry-run evaluator with its owned transport. Disposing releases the
/// provider's HTTP handler; the ledger and decision log hold no resources.
/// </summary>
public sealed class ArbiterEvaluatorSession : IDisposable
{
    private readonly IDisposable? _owned;
    private bool _disposed;

    /// <summary>Creates a session over an evaluator and its optionally owned transport.</summary>
    public ArbiterEvaluatorSession(ArbiterEvaluator evaluator, IDisposable? owned)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        Evaluator = evaluator;
        _owned = owned;
    }

    /// <summary>The evaluator to run the dry run through.</summary>
    public ArbiterEvaluator Evaluator { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _owned?.Dispose();
    }
}
