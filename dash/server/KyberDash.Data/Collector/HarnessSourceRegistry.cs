using System.Globalization;
using System.Text;
using KyberDash.Data.Harnesses.Cursor;
using KyberDash.Data.Harnesses.Devin;
using KyberDash.Data.Harnesses.Kilo;
using KyberDash.Data.Harnesses.Warp;
using KyberDash.Data.Repositories;
using KyberDash.Domain;

namespace KyberDash.Data.Collector.Sources;

/// <summary>
/// Support classification of a registered harness source entry inside <see cref="HarnessSourceRegistry"/>.
/// </summary>
public enum HarnessSourceSupport
{
    /// <summary>Evidenced local transcript, JSON, or SQLite source files discovered on disk.</summary>
    LocalFiles,

    /// <summary>OTLP-only telemetry source with no local transcript files on disk.</summary>
    OtlpOnly,

    /// <summary>Classified harness surface sharing or receiving telemetry without its own local root.</summary>
    ClassifiedOnly,

    /// <summary>Known harness identifier whose local persistence format is unevidenced.</summary>
    Unevidenced,
}

/// <summary>
/// Registration metadata returned by <see cref="HarnessSourceRegistry.Resolve"/> for a canonical harness ID.
/// </summary>
/// <param name="CanonicalHarnessId">Canonical normalized harness identifier.</param>
/// <param name="Support">Support classification for the harness.</param>
/// <param name="Reader">Source reader when evidenced; <see langword="null"/> when unevidenced or classified-only.</param>
public record HarnessSourceRegistration(
    string CanonicalHarnessId,
    HarnessSourceSupport Support,
    IHarnessSourceReader? Reader);

/// <summary>
/// Central registry of local and OTLP harness source readers enforcing BL-2 coverage,
/// <see cref="CanonPricing.NormalizeHarnessName"/> alias folding, <c>zcode</c> unevidenced
/// diagnostics, and BL-3 exclusions (<c>windsurf</c>, <c>aider</c>).
/// </summary>
public class HarnessSourceRegistry
{
    /// <summary>Ingest problem code emitted when a harness format is unevidenced or unknown.</summary>
    public const string UnevidencedSourceFormatCode = "SOURCE_FORMAT_UNEVIDENCED";

    /// <summary>Default parser contract version stamped onto discovered source units.</summary>
    public const string DefaultParserContractVersion = "1";

    /// <summary>
    /// Parser contract version of the <c>droid</c>, <c>cursor</c> and <c>warp</c> readers, whose
    /// mappers stopped splitting or guessing token counts (decision D15).
    /// </summary>
    /// <remarks>
    /// A unit's version is part of its checkpoint, and <c>CanonRefreshEngine.CheckpointIsReusable</c>
    /// refuses a checkpoint stamped with another version, so the next refresh re-reads these units
    /// and replaces the estimated token rows stored under version <see cref="DefaultParserContractVersion"/>.
    /// </remarks>
    public const string NoEstimatedTokensParserContractVersion = "2";

    /// <summary>
    /// Parser contract version of the <c>roo-code</c> reader, whose mapper now takes the task id from
    /// the task directory rather than from the <c>ui_messages.json</c> file name.
    /// </summary>
    /// <remarks>Stored Roo rows were all keyed to one task id; the new version makes the next refresh re-read them.</remarks>
    public const string RooTaskDirectoryIdParserContractVersion = "3";

    /// <summary>
    /// Parser contract version of the <c>copilot-cli</c> reader, whose mapper now stores fresh input as
    /// input minus cache read and cache write.
    /// </summary>
    /// <remarks>
    /// Rows imported under <see cref="DefaultParserContractVersion"/> stored fresh input inclusive of
    /// cache tokens. Bumping the version makes <c>CanonRefreshEngine.CheckpointIsReusable</c> refuse the
    /// stored checkpoint, so the next refresh re-reads those sessions under the fresh-input mapping.
    /// </remarks>
    public const string CopilotCliFreshInputParserContractVersion = "2";

    /// <summary>
    /// Parser contract version of the <c>kilo-sqlite</c>, <c>warp-sqlite</c>, <c>cursor-vscdb</c> and
    /// <c>devin-sqlite</c> readers, which now read one conversation row export per unit (decision D7).
    /// </summary>
    /// <remarks>
    /// The units are new because their <c>SourceKey</c> now ends in <c>#&lt;conversation id&gt;</c>, so the
    /// version is belt and braces: it also keeps a checkpoint written by an older build, under
    /// <see cref="NoEstimatedTokensParserContractVersion"/> or <see cref="DefaultParserContractVersion"/>,
    /// from being reused for a row export.
    /// </remarks>
    public const string SqliteRowExportParserContractVersion = "4";

    private static readonly HashSet<string> ExcludedHarnessIds = new(
        ["windsurf", "aider"],
        StringComparer.Ordinal);

    private readonly Dictionary<string, HarnessSourceRegistration> registrations;
    private readonly IReadOnlyList<IHarnessSourceReader> localReaders;

    /// <summary>Initializes the registry with all BL-2, evidenced legacy, and OTLP-only harness registrations.</summary>
    /// <param name="sqlite">Read-only SQLite seam for the Kilo, Warp, Devin and Cursor databases; defaults to <see cref="MicrosoftDataSqliteHarnessSource.CreateDefault"/>.</param>
    public HarnessSourceRegistry(IHarnessSqliteSource? sqlite = null)
    {
        IHarnessSqliteSource sqliteSource = sqlite ?? MicrosoftDataSqliteHarnessSource.CreateDefault();
        List<IHarnessSourceReader> local =
        [
            new SpecHarnessSourceReader(
                TranscriptReaderRepository.ClaudeHarnessId,
                new SourceRootSpec(
                    TranscriptReaderRepository.ClaudeProviderId,
                    TranscriptReaderRepository.ClaudeParserId,
                    TranscriptReaderRepository.ClaudeFormat,
                    [".claude/projects"],
                    ["*.jsonl"])),
            new SpecHarnessSourceReader(
                TranscriptReaderRepository.CodexHarnessId,
                new SourceRootSpec(
                    TranscriptReaderRepository.CodexProviderId,
                    TranscriptReaderRepository.CodexParserId,
                    TranscriptReaderRepository.CodexFormat,
                    [".codex/sessions"],
                    ["*.jsonl"])),
            new SpecHarnessSourceReader(
                "copilot-cli",
                new SourceRootSpec(
                    "copilot",
                    "copilot-cli-reader",
                    "copilot-cli-jsonl",
                    [".copilot/session-state"],
                    ["events.jsonl"],
                    ParserContractVersion: CopilotCliFreshInputParserContractVersion)),
            new AntigravityHarnessSourceReader(),
            new SpecHarnessSourceReader(
                "antigravity-cli",
                new SourceRootSpec("google", "antigravity-cli-reader", "antigravity-pb", [".gemini/antigravity-cli/conversations"], ["*.pb"]),
                new SourceRootSpec("google", "antigravity-cli-reader", "antigravity-jsonl", [".gemini/antigravity-cli/brain"], ["*.jsonl"])),
            new SpecHarnessSourceReader(
                "commandcode",
                new SourceRootSpec("commandcode", "commandcode-reader", "commandcode-jsonl", [".commandcode/projects"], ["*.jsonl"])),
            CreateSqliteReader(
                "kilo-shared-runtime",
                "kilo",
                "kilo-sqlite-reader",
                "kilo-sqlite",
                [
                    ".local/share/kilo/kilo.db",
                    "Library/Application Support/kilo/kilo.db",
                    "AppData/Local/kilo/kilo.db",
                    "AppData/Roaming/kilo/kilo.db",
                ],
                new KiloSqliteConversationSource(),
                sqliteSource),
            new SpecHarnessSourceReader(
                "kilo-vscode-legacy",
                new SourceRootSpec(
                    "kilo",
                    "kilo-vscode-reader",
                    "kilo-ui-messages-json",
                    [
                        "Library/Application Support/Code/User/globalStorage/kilocode.kilo-code/tasks",
                        ".config/Code/User/globalStorage/kilocode.kilo-code/tasks",
                        "AppData/Roaming/Code/User/globalStorage/kilocode.kilo-code/tasks",
                    ],
                    ["ui_messages.json"])),
            new DevinHarnessSourceReader(
                new SpecHarnessSourceReader(
                    "devin",
                    new SourceRootSpec("devin", "devin-reader", "devin-transcript-json", [".local/share/devin/cli/transcripts"], ["*.json", "*.jsonl"])),
                CreateSqliteReader(
                    "devin",
                    "devin",
                    "devin-reader",
                    "devin-sqlite",
                    [".local/share/devin/cli/sessions.db"],
                    new DevinSqliteConversationSource(),
                    sqliteSource)),
            CreateSqliteReader(
                "warp",
                "warp",
                "warp-sqlite-reader",
                "warp-sqlite",
                [
                    "Library/Group Containers/2BBY89MBSN.dev.warp/Library/Application Support/dev.warp.Warp-Stable/warp.sqlite",
                    "Library/Group Containers/2BBY89MBSN.dev.warp/Library/Application Support/dev.warp.Warp-Preview/warp.sqlite",
                    ".local/state/warp-terminal/warp.sqlite",
                    "AppData/Local/warp/Warp/data/warp.sqlite",
                ],
                new WarpSqliteConversationSource(),
                sqliteSource),
            new SpecHarnessSourceReader(
                "droid",
                new SourceRootSpec(
                    "factory",
                    "droid-reader",
                    "droid-jsonl",
                    [".factory/sessions"],
                    ["*.jsonl"],
                    CompanionExtension: ".settings.json",
                    ParserContractVersion: NoEstimatedTokensParserContractVersion)),
            new CursorHarnessSourceReader(
                CreateSqliteReader(
                    "cursor",
                    "cursor",
                    "cursor-vscdb-reader",
                    "cursor-vscdb",
                    [
                        "Library/Application Support/Cursor/User/globalStorage/state.vscdb",
                        ".config/Cursor/User/globalStorage/state.vscdb",
                        "AppData/Roaming/Cursor/User/globalStorage/state.vscdb",
                    ],
                    new CursorSqliteConversationSource(),
                    sqliteSource)),
            new SpecHarnessSourceReader(
                "cline",
                new SourceRootSpec(
                    "cline",
                    "cline-reader",
                    "cline-ui-messages-json",
                    [
                        "Library/Application Support/Code/User/globalStorage/saoudrizwan.claude-dev/tasks",
                        ".config/Code/User/globalStorage/saoudrizwan.claude-dev/tasks",
                        "AppData/Roaming/Code/User/globalStorage/saoudrizwan.claude-dev/tasks",
                    ],
                    ["ui_messages.json"])),
            new SpecHarnessSourceReader(
                "cline-cli",
                new SourceRootSpec("cline", "cline-cli-reader", "cline-cli-messages-json", [".cline/data/sessions"], ["*.messages.json"])),
            new SpecHarnessSourceReader(
                "roo-code",
                new SourceRootSpec(
                    "roo-code",
                    "roo-code-reader",
                    "roo-ui-messages-json",
                    [
                        "Library/Application Support/Code/User/globalStorage/rooveterinaryinc.roo-cline/tasks",
                        ".config/Code/User/globalStorage/rooveterinaryinc.roo-cline/tasks",
                        "AppData/Roaming/Code/User/globalStorage/rooveterinaryinc.roo-cline/tasks",
                    ],
                    ["ui_messages.json"],
                    ParserContractVersion: RooTaskDirectoryIdParserContractVersion)),
            new CopilotVsCodeHarnessSourceReader(),
            new SpecHarnessSourceReader(
                "pi",
                new SourceRootSpec("pi", "pi-reader", "pi-jsonl", [".pi/agent/sessions"], ["*.jsonl"])),
            new SpecHarnessSourceReader(
                "kimi-code",
                new SourceRootSpec("kimi", "kimi-code-reader", "kimi-wire-jsonl", [".kimi-code/sessions", ".kimi/sessions"], ["wire.jsonl"])),
        ];

        localReaders = local;
        registrations = new Dictionary<string, HarnessSourceRegistration>(StringComparer.Ordinal);

        foreach (IHarnessSourceReader reader in local)
        {
            registrations[reader.HarnessId] = new HarnessSourceRegistration(
                reader.HarnessId,
                HarnessSourceSupport.LocalFiles,
                reader);
        }

        foreach (string otlpHarnessId in new[] { "opencode", "copilot", "copilot-agent" })
        {
            OtlpOnlyHarnessSourceReader reader = new(otlpHarnessId);
            registrations[otlpHarnessId] = new HarnessSourceRegistration(
                otlpHarnessId,
                HarnessSourceSupport.OtlpOnly,
                reader);
        }

        foreach (string classifiedOnlyId in new[] { "claude-unclassified", "codex-desktop" })
        {
            registrations[classifiedOnlyId] = new HarnessSourceRegistration(
                classifiedOnlyId,
                HarnessSourceSupport.ClassifiedOnly,
                null);
        }

        registrations["zcode"] = new HarnessSourceRegistration(
            "zcode",
            HarnessSourceSupport.Unevidenced,
            null);
    }

    /// <summary>Normalizes a raw harness identifier via <see cref="CanonPricing.NormalizeHarnessName"/>.</summary>
    public static string NormalizeHarnessId(string harnessId)
    {
        ArgumentNullException.ThrowIfNull(harnessId);
        return CanonPricing.NormalizeHarnessName(harnessId);
    }

    /// <summary>Returns <see langword="true"/> when <paramref name="harnessId"/> belongs to the BL-3 excluded set.</summary>
    public bool IsExcluded(string harnessId)
    {
        if (string.IsNullOrWhiteSpace(harnessId))
        {
            return false;
        }

        return ExcludedHarnessIds.Contains(NormalizeHarnessId(harnessId));
    }

    /// <summary>Returns <see langword="true"/> when <paramref name="harnessId"/> is registered as OTLP-only.</summary>
    public bool IsOtlpOnly(string harnessId) =>
        Resolve(harnessId) is { Support: HarnessSourceSupport.OtlpOnly };

    /// <summary>Resolves registration metadata for <paramref name="harnessId"/> after alias folding, or <see langword="null"/> when unknown or excluded.</summary>
    public HarnessSourceRegistration? Resolve(string harnessId)
    {
        if (string.IsNullOrWhiteSpace(harnessId))
        {
            return null;
        }

        string normalized = NormalizeHarnessId(harnessId);
        if (ExcludedHarnessIds.Contains(normalized))
        {
            return null;
        }

        return registrations.GetValueOrDefault(normalized);
    }

    /// <summary>Returns the evidenced <see cref="IHarnessSourceReader"/> for <paramref name="harnessId"/>, or <see langword="null"/> when excluded, unevidenced, or unknown.</summary>
    public IHarnessSourceReader? GetReader(string harnessId)
    {
        HarnessSourceRegistration? registration = Resolve(harnessId);
        return registration?.Reader;
    }

    /// <summary>Discovers all evidenced local source units and any <c>zcode</c> unevidenced database diagnostics under <paramref name="context"/>.</summary>
    public SourceDiscoveryResult DiscoverAll(SourceDiscoveryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.FileSystem);

        List<SourceUnit> sources = [];
        List<CollectedProblem> problems = [];
        HashSet<string> seenSourceKeys = new(StringComparer.Ordinal);

        foreach (IHarnessSourceReader reader in localReaders)
        {
            SourceDiscoveryResult perReader = reader.Discover(context);
            AppendUniqueSources(sources, seenSourceKeys, perReader.Sources);
            problems.AddRange(perReader.Problems);
        }

        string? zcodePath = FindZcodeDatabasePath(context);
        if (zcodePath is not null)
        {
            problems.Add(BuildUnevidencedProblem("zcode", zcodePath));
        }

        return new SourceDiscoveryResult(sources, problems);
    }

    /// <summary>Discovers source units or unevidenced diagnostics for a single <paramref name="harnessId"/> under <paramref name="context"/>.</summary>
    public SourceDiscoveryResult DiscoverForHarness(string harnessId, SourceDiscoveryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.FileSystem);

        if (string.IsNullOrWhiteSpace(harnessId))
        {
            return new SourceDiscoveryResult([], [BuildUnevidencedProblem("unknown", null)]);
        }

        string normalized = NormalizeHarnessId(harnessId);
        if (ExcludedHarnessIds.Contains(normalized))
        {
            return new SourceDiscoveryResult([], []);
        }

        if (!registrations.TryGetValue(normalized, out HarnessSourceRegistration? registration))
        {
            return new SourceDiscoveryResult([], [BuildUnevidencedProblem(normalized, null)]);
        }

        if (registration.Support == HarnessSourceSupport.Unevidenced)
        {
            string? zcodePath = string.Equals(normalized, "zcode", StringComparison.Ordinal)
                ? FindZcodeDatabasePath(context)
                : null;
            return new SourceDiscoveryResult([], [BuildUnevidencedProblem(normalized, zcodePath)]);
        }

        if (registration.Reader is null)
        {
            return new SourceDiscoveryResult([], []);
        }

        return registration.Reader.Discover(context);
    }

    private static void AppendUniqueSources(
        List<SourceUnit> target,
        HashSet<string> seenKeys,
        IReadOnlyList<SourceUnit> candidates)
    {
        foreach (SourceUnit unit in candidates)
        {
            if (seenKeys.Add(unit.SourceKey))
            {
                target.Add(unit);
            }
        }
    }

    private static string? FindZcodeDatabasePath(SourceDiscoveryContext context)
    {
        string dbPath = CombineRelative(context.HomeDirectory, ".zcode/cli/db/db.sqlite");
        if (context.FileSystem.FileExists(dbPath))
        {
            return context.FileSystem.GetFullPath(dbPath);
        }

        string dbDirectory = CombineRelative(context.HomeDirectory, ".zcode/cli/db");
        if (!context.FileSystem.DirectoryExists(dbDirectory))
        {
            return null;
        }

        foreach (string pattern in new[] { "*.sqlite", "*.db" })
        {
            string? first = context.FileSystem
                .EnumerateFiles(dbDirectory, pattern, recursive: false)
                .OrderBy(static p => p, StringComparer.Ordinal)
                .FirstOrDefault();
            if (first is not null)
            {
                return context.FileSystem.GetFullPath(first);
            }
        }

        return null;
    }

    private static CollectedProblem BuildUnevidencedProblem(string normalizedHarnessId, string? location)
    {
        string keySuffix = string.IsNullOrWhiteSpace(location) ? "registry" : location;
        string message = location is null
            ? $"Source format for harness '{normalizedHarnessId}' is unevidenced and cannot be parsed by local discovery."
            : $"Source format for harness '{normalizedHarnessId}' at '{location}' is unevidenced and cannot be parsed by local discovery.";

        return new CollectedProblem(
            ProblemKey: $"source-format-unevidenced:{normalizedHarnessId}:{keySuffix}",
            Severity: "warning",
            Code: UnevidencedSourceFormatCode,
            Message: message,
            Location: location,
            HarnessId: normalizedHarnessId);
    }

    private static string CombineRelative(string root, string relative)
    {
        string normalizedRelative = relative.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(root, normalizedRelative);
    }

    private static SourceUnit? TryBuildFileUnit(
        SourceDiscoveryContext context,
        string filePath,
        string harnessId,
        string providerId,
        string parserId,
        string parserContractVersion,
        string format,
        string? companionExtension = null,
        string? rootLabelOverride = null)
    {
        IHarnessFileSystem fs = context.FileSystem;
        string fullPath = fs.GetFullPath(filePath);
        bool hasStats = fs.TryGetFileStats(fullPath, out long lengthBytes, out DateTimeOffset lastWriteUtc);

        if (!hasStats)
        {
            if (context.SinceUtc.HasValue)
            {
                string unknownRevision = fs.FileExists(fullPath) ? "unknown" : "missing";
                return new SourceUnit(
                    HarnessId: harnessId,
                    SourceKey: fullPath,
                    ProviderId: providerId,
                    ParserId: parserId,
                    ParserContractVersion: parserContractVersion,
                    Format: format,
                    SourceRootLabel: rootLabelOverride ?? ParentLabel(fullPath),
                    RevisionToken: unknownRevision);
            }

            return null;
        }

        if (context.SinceUtc.HasValue && lastWriteUtc < context.SinceUtc.Value)
        {
            return null;
        }

        string revision = string.Create(CultureInfo.InvariantCulture, $"{lengthBytes}-{lastWriteUtc.UtcTicks}");
        string? companionKey = ResolveCompanionKey(fs, fullPath, companionExtension);

        return new SourceUnit(
            HarnessId: harnessId,
            SourceKey: fullPath,
            ProviderId: providerId,
            ParserId: parserId,
            ParserContractVersion: parserContractVersion,
            Format: format,
            SourceRootLabel: rootLabelOverride ?? ParentLabel(fullPath),
            RevisionToken: revision,
            LastWriteUtc: lastWriteUtc,
            SizeBytes: lengthBytes,
            CompanionKey: companionKey,
            Kind: SourceKind.File);
    }

    private static string? ResolveCompanionKey(
        IHarnessFileSystem fs,
        string fullPath,
        string? companionExtension)
    {
        if (string.IsNullOrEmpty(companionExtension))
        {
            return null;
        }

        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        string stem = Path.GetFileNameWithoutExtension(fullPath);
        string candidate = Path.Combine(directory, stem + companionExtension);
        return fs.FileExists(candidate) ? fs.GetFullPath(candidate) : null;
    }

    private static string ParentLabel(string fullPath) =>
        Path.GetFileName(Path.GetDirectoryName(fullPath) ?? string.Empty);

    private static NativeSourceUnit? ReadLocalUnit(SourceUnit unit, IHarnessFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(fileSystem);

        if (!fileSystem.FileExists(unit.SourceKey))
        {
            return null;
        }

        bool hasStats = fileSystem.TryGetFileStats(unit.SourceKey, out long lengthBytes, out DateTimeOffset lastWriteUtc);
        string? rawText = null;
        if (!IsBinaryFormat(unit.Format))
        {
            rawText = fileSystem.ReadAllText(unit.SourceKey);
            if (!hasStats || lengthBytes == 0)
            {
                lengthBytes = Encoding.UTF8.GetByteCount(rawText);
            }
        }

        string? companionText = null;
        if (!string.IsNullOrWhiteSpace(unit.CompanionKey) && fileSystem.FileExists(unit.CompanionKey))
        {
            companionText = fileSystem.ReadAllText(unit.CompanionKey);
        }

        DateTimeOffset effectiveWriteUtc = hasStats ? lastWriteUtc : (unit.LastWriteUtc ?? default);
        return new NativeSourceUnit(
            Unit: unit,
            RawText: rawText,
            SizeBytes: lengthBytes,
            LastWriteUtc: effectiveWriteUtc,
            CompanionText: companionText);
    }

    private static bool IsBinaryFormat(string format) =>
        format.EndsWith("-pb", StringComparison.Ordinal);

    private static SqliteHarnessSourceReader CreateSqliteReader(
        string harnessId,
        string providerId,
        string parserId,
        string format,
        IReadOnlyList<string> explicitRelativeDatabasePaths,
        ISqliteConversationSource conversations,
        IHarnessSqliteSource sqlite) =>
        new(
            harnessId,
            providerId,
            parserId,
            format,
            SqliteRowExportParserContractVersion,
            explicitRelativeDatabasePaths,
            conversations,
            sqlite,
            new HarnessSqliteLimits());

    private sealed record SourceRootSpec(
        string ProviderId,
        string ParserId,
        string Format,
        IReadOnlyList<string> RelativeRoots,
        IReadOnlyList<string> SearchPatterns,
        IReadOnlyList<string>? ExplicitFiles = null,
        string? CompanionExtension = null,
        string ParserContractVersion = DefaultParserContractVersion);

    private sealed class OtlpOnlyHarnessSourceReader(string harnessId) : IHarnessSourceReader
    {
        public string HarnessId { get; } = harnessId;

        public bool IsOtlpOnly => true;

        public SourceDiscoveryResult Discover(SourceDiscoveryContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return new SourceDiscoveryResult([], []);
        }

        public NativeSourceUnit? Read(SourceUnit unit, IHarnessFileSystem fileSystem)
        {
            ArgumentNullException.ThrowIfNull(unit);
            ArgumentNullException.ThrowIfNull(fileSystem);
            return null;
        }
    }

    private sealed class SpecHarnessSourceReader(string harnessId, params SourceRootSpec[] specs) : IHarnessSourceReader
    {
        public string HarnessId { get; } = harnessId;

        public bool IsOtlpOnly => false;

        public SourceDiscoveryResult Discover(SourceDiscoveryContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            List<SourceUnit> units = [];
            HashSet<string> seen = new(StringComparer.Ordinal);

            foreach (SourceRootSpec spec in specs)
            {
                foreach (string relativeRoot in spec.RelativeRoots)
                {
                    string rootPath = CombineRelative(context.HomeDirectory, relativeRoot);
                    if (!context.FileSystem.DirectoryExists(rootPath))
                    {
                        continue;
                    }

                    foreach (string pattern in spec.SearchPatterns)
                    {
                        foreach (string file in context.FileSystem.EnumerateFiles(rootPath, pattern, recursive: true))
                        {
                            AddIfValid(context, units, seen, file, spec);
                        }
                    }
                }

                if (spec.ExplicitFiles is not null)
                {
                    foreach (string relativeFile in spec.ExplicitFiles)
                    {
                        string filePath = CombineRelative(context.HomeDirectory, relativeFile);
                        if (context.FileSystem.FileExists(filePath))
                        {
                            AddIfValid(context, units, seen, filePath, spec);
                        }
                    }
                }
            }

            return new SourceDiscoveryResult(units, []);
        }

        public NativeSourceUnit? Read(SourceUnit unit, IHarnessFileSystem fileSystem) =>
            ReadLocalUnit(unit, fileSystem);

        private void AddIfValid(
            SourceDiscoveryContext context,
            List<SourceUnit> units,
            HashSet<string> seen,
            string filePath,
            SourceRootSpec spec)
        {
            SourceUnit? unit = TryBuildFileUnit(
                context,
                filePath,
                HarnessId,
                spec.ProviderId,
                spec.ParserId,
                spec.ParserContractVersion,
                spec.Format,
                spec.CompanionExtension);
            if (unit is not null && seen.Add(unit.SourceKey))
            {
                units.Add(unit);
            }
        }
    }

    private sealed class AntigravityHarnessSourceReader : IHarnessSourceReader
    {
        private readonly SpecHarnessSourceReader transcriptReader = new(
            "antigravity",
            new SourceRootSpec("google", "antigravity-reader", "antigravity-pb", [".gemini/antigravity/conversations"], ["*.pb"]),
            new SourceRootSpec("google", "antigravity-reader", "antigravity-jsonl", [".gemini/antigravity/brain"], ["*.jsonl"]));

        public string HarnessId => "antigravity";

        public bool IsOtlpOnly => false;

        public SourceDiscoveryResult Discover(SourceDiscoveryContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            SourceDiscoveryResult baseResult = transcriptReader.Discover(context);
            List<SourceUnit> units = [.. baseResult.Sources];
            HashSet<string> seen = new(units.Select(static u => u.SourceKey), StringComparer.Ordinal);

            if (!string.IsNullOrWhiteSpace(context.StateDirectory))
            {
                string statusLinePath = CombineRelative(
                    context.StateDirectory,
                    SettingsRepository.AntigravityStatusLineRelativePath);
                if (context.FileSystem.FileExists(statusLinePath))
                {
                    SourceUnit? statusUnit = TryBuildFileUnit(
                        context,
                        statusLinePath,
                        HarnessId,
                        "google",
                        "antigravity-statusline-reader",
                        DefaultParserContractVersion,
                        "antigravity-statusline-jsonl",
                        rootLabelOverride: "cache");
                    if (statusUnit is not null && seen.Add(statusUnit.SourceKey))
                    {
                        units.Add(statusUnit);
                    }
                }
            }

            return new SourceDiscoveryResult(units, []);
        }

        public NativeSourceUnit? Read(SourceUnit unit, IHarnessFileSystem fileSystem) =>
            ReadLocalUnit(unit, fileSystem);
    }

    /// <summary>
    /// Reads Devin transcripts through the file specs and <c>sessions.db</c> through the SQLite reader,
    /// routing <see cref="Read"/> by <see cref="SourceUnit.Format"/>.
    /// </summary>
    private sealed class DevinHarnessSourceReader(
        SpecHarnessSourceReader transcriptReader,
        SqliteHarnessSourceReader sqliteReader) : IHarnessSourceReader
    {
        public string HarnessId => "devin";

        public bool IsOtlpOnly => false;

        public SourceDiscoveryResult Discover(SourceDiscoveryContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            SourceDiscoveryResult transcripts = transcriptReader.Discover(context);
            SourceDiscoveryResult databases = sqliteReader.Discover(context);
            return new SourceDiscoveryResult(
                [.. transcripts.Sources, .. databases.Sources],
                [.. transcripts.Problems, .. databases.Problems]);
        }

        public NativeSourceUnit? Read(SourceUnit unit, IHarnessFileSystem fileSystem)
        {
            ArgumentNullException.ThrowIfNull(unit);
            return string.Equals(unit.Format, "devin-sqlite", StringComparison.Ordinal)
                ? sqliteReader.Read(unit, fileSystem)
                : transcriptReader.Read(unit, fileSystem);
        }
    }

    private sealed class CursorHarnessSourceReader(SqliteHarnessSourceReader vscdbReader) : IHarnessSourceReader
    {
        public string HarnessId => "cursor";

        public bool IsOtlpOnly => false;

        public SourceDiscoveryResult Discover(SourceDiscoveryContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            SourceDiscoveryResult databases = vscdbReader.Discover(context);
            List<SourceUnit> units = [.. databases.Sources];
            HashSet<string> seen = new(units.Select(static u => u.SourceKey), StringComparer.Ordinal);

            string projectsRoot = CombineRelative(context.HomeDirectory, ".cursor/projects");
            if (context.FileSystem.DirectoryExists(projectsRoot))
            {
                foreach (string projectDir in context.FileSystem.EnumerateDirectories(projectsRoot))
                {
                    string transcriptsDir = Path.Combine(projectDir, "agent-transcripts");
                    if (!context.FileSystem.DirectoryExists(transcriptsDir))
                    {
                        continue;
                    }

                    foreach (string file in context.FileSystem.EnumerateFiles(transcriptsDir, "*.jsonl", recursive: true))
                    {
                        SourceUnit? unit = TryBuildFileUnit(
                            context,
                            file,
                            HarnessId,
                            "cursor",
                            "cursor-agent-transcript-reader",
                            NoEstimatedTokensParserContractVersion,
                            "cursor-agent-jsonl");
                        if (unit is not null && seen.Add(unit.SourceKey))
                        {
                            units.Add(unit);
                        }
                    }
                }
            }

            return new SourceDiscoveryResult(units, databases.Problems);
        }

        public NativeSourceUnit? Read(SourceUnit unit, IHarnessFileSystem fileSystem)
        {
            ArgumentNullException.ThrowIfNull(unit);
            return string.Equals(unit.Format, "cursor-vscdb", StringComparison.Ordinal)
                ? vscdbReader.Read(unit, fileSystem)
                : ReadLocalUnit(unit, fileSystem);
        }
    }

    private sealed class CopilotVsCodeHarnessSourceReader : IHarnessSourceReader
    {
        private static readonly string[] WorkspaceStorageRoots =
        [
            "Library/Application Support/Code/User/workspaceStorage",
            ".config/Code/User/workspaceStorage",
            "AppData/Roaming/Code/User/workspaceStorage",
        ];

        private static readonly string[] EmptyWindowRoots =
        [
            "Library/Application Support/Code/User/globalStorage/emptyWindowChatSessions",
            ".config/Code/User/globalStorage/emptyWindowChatSessions",
            "AppData/Roaming/Code/User/globalStorage/emptyWindowChatSessions",
        ];

        public string HarnessId => "copilot-vscode";

        public bool IsOtlpOnly => false;

        public SourceDiscoveryResult Discover(SourceDiscoveryContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            List<SourceUnit> units = [];
            HashSet<string> seen = new(StringComparer.Ordinal);

            foreach (string relativeRoot in WorkspaceStorageRoots)
            {
                string workspaceRoot = CombineRelative(context.HomeDirectory, relativeRoot);
                if (!context.FileSystem.DirectoryExists(workspaceRoot))
                {
                    continue;
                }

                foreach (string workspaceDir in context.FileSystem.EnumerateDirectories(workspaceRoot))
                {
                    string chatSessionsDir = Path.Combine(workspaceDir, "chatSessions");
                    if (context.FileSystem.DirectoryExists(chatSessionsDir))
                    {
                        CollectChatFiles(context, chatSessionsDir, units, seen);
                    }
                }
            }

            foreach (string relativeEmptyRoot in EmptyWindowRoots)
            {
                string emptyRoot = CombineRelative(context.HomeDirectory, relativeEmptyRoot);
                if (context.FileSystem.DirectoryExists(emptyRoot))
                {
                    CollectChatFiles(context, emptyRoot, units, seen);
                }
            }

            return new SourceDiscoveryResult(units, []);
        }

        public NativeSourceUnit? Read(SourceUnit unit, IHarnessFileSystem fileSystem) =>
            ReadLocalUnit(unit, fileSystem);

        private void CollectChatFiles(
            SourceDiscoveryContext context,
            string chatSessionsDir,
            List<SourceUnit> units,
            HashSet<string> seen)
        {
            foreach (string pattern in new[] { "*.jsonl", "*.json" })
            {
                foreach (string file in context.FileSystem.EnumerateFiles(chatSessionsDir, pattern, recursive: true))
                {
                    SourceUnit? unit = TryBuildFileUnit(
                        context,
                        file,
                        HarnessId,
                        "copilot",
                        "copilot-vscode-reader",
                        DefaultParserContractVersion,
                        "copilot-vscode-journal");
                    if (unit is not null && seen.Add(unit.SourceKey))
                    {
                        units.Add(unit);
                    }
                }
            }
        }
    }
}
