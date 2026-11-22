using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KyberDash.Core.Exceptions;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Domain;

namespace KyberDash.Data.Harnesses.Otlp;

/// <summary>
/// Shared <see cref="IHarnessMapper"/> pipeline for OTLP span mappers.
/// Each mapper declares only its wire contract (the canonical harness id, its
/// <see cref="OtlpSpanMapperCore.OtlpMapperHooks"/>, and any <see cref="Parse"/> quirks);
/// the unit-decode, batch, and single-span delegation lives here so the mappers cannot drift apart.
/// </summary>
public abstract class OtlpSpanMapperBase : IHarnessMapper
{
    /// <summary>Problem code emitted when the OTLP trace payload fails to decode.</summary>
    protected const string MalformedOtlpCode = "MALFORMED_OTLP_PAYLOAD";

    /// <summary>Canonical harness identifier emitted by this mapper.</summary>
    protected abstract string CanonicalHarnessId { get; }

    private readonly OtlpSpanMapperCore.OtlpMapperHooks _hooks;

    /// <summary>
    /// Initializes a new instance of the <see cref="OtlpSpanMapperBase"/> class with the
    /// per-harness wire contract delegated to <see cref="OtlpSpanMapperCore"/>.
    /// </summary>
    /// <param name="hooks">Per-harness wire contract.</param>
    internal OtlpSpanMapperBase(OtlpSpanMapperCore.OtlpMapperHooks hooks)
    {
        ArgumentNullException.ThrowIfNull(hooks);
        _hooks = hooks;
    }

    /// <inheritdoc />
    public string HarnessId => CanonicalHarnessId;

    /// <summary>
    /// Parses <paramref name="span"/> into an <see cref="OtlpSpanNative"/> using the canonical
    /// OTLP attribute key lists. Mappers with extra content keys override this.
    /// </summary>
    /// <param name="span">Decoded OTLP span.</param>
    /// <returns>Extracted <see cref="OtlpSpanNative"/>.</returns>
    public virtual OtlpSpanNative Parse(OtlpSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);
        return OtlpSpanNative.Parse(span);
    }

    /// <summary>
    /// Maps a single decoded OTLP <paramref name="span"/> into a normalized
    /// <see cref="CollectedRecord"/>.
    /// </summary>
    /// <param name="span">Decoded OTLP span.</param>
    /// <returns>Normalized <see cref="CollectedRecord"/>.</returns>
    public CollectedRecord MapSpan(OtlpSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);
        OtlpSpanNative native = Parse(span);
        return OtlpSpanMapperCore.MapNative(native, CanonicalHarnessId, _hooks);
    }

    /// <summary>
    /// Maps a batch of decoded OTLP <paramref name="spans"/> into sessions, validated records,
    /// and token/ingest problems.
    /// </summary>
    /// <param name="spans">Decoded OTLP spans.</param>
    /// <param name="sourceLocator">Optional source locator override; defaults to the first span's <c>service.name</c> or <c>"otlp"</c>.</param>
    /// <param name="harnessIdOverride">Optional harness identifier override; defaults to <see cref="HarnessId"/>.</param>
    /// <returns>Static <see cref="CollectedBatch"/> for persistence.</returns>
    public CollectedBatch MapSpans(
        IReadOnlyList<OtlpSpan> spans,
        string? sourceLocator = null,
        string? harnessIdOverride = null)
    {
        ArgumentNullException.ThrowIfNull(spans);

        string harnessId = ResolveHarnessId(harnessIdOverride);
        return OtlpSpanMapperCore.MapSpans(spans, Parse, sourceLocator, harnessId, _hooks);
    }

    /// <inheritdoc />
    public CollectedBatch Map(NativeSourceUnit unit, DateTimeOffset importedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(unit.Unit);

        string harnessId = ResolveHarnessId(unit.Unit.HarnessId);
        CollectedSource source = new(harnessId, unit.Unit.Kind, unit.Unit.SourceKey);

        if (string.IsNullOrWhiteSpace(unit.RawText))
        {
            return new CollectedBatch(source, [], [], []);
        }

        IReadOnlyList<OtlpSpan> spans;
        try
        {
            spans = OtlpCollector.DecodeTracesJson(Encoding.UTF8.GetBytes(unit.RawText));
        }
        catch (Exception ex) when (ex is OtlpDecodeException or OtlpPayloadTooLargeException or JsonException)
        {
            string digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(unit.Unit.SourceKey)))[..16];
            string message = OtlpSpanMapperCore.SafeOptionalText(ex.Message) ?? "Failed to decode OTLP trace payload.";
            CollectedProblem problem = new(
                ProblemKey: $"otlp:{digest}:{MalformedOtlpCode}",
                Severity: OtlpSpanMapperCore.FormatProblemSeverity(ProblemSeverity.Error),
                Code: MalformedOtlpCode,
                Message: message,
                Location: OtlpSpanMapperCore.SafeOptionalText(unit.Unit.SourceKey),
                HarnessId: harnessId,
                TimestampUtc: importedAtUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            return new CollectedBatch(source, [], [], [problem]);
        }

        return OtlpSpanMapperCore.MapSpans(spans, Parse, unit.Unit.SourceKey, harnessId, _hooks);
    }

    private string ResolveHarnessId(string? unitHarnessId)
    {
        if (string.IsNullOrWhiteSpace(unitHarnessId))
        {
            return CanonicalHarnessId;
        }

        string normalized = HarnessSourceRegistry.NormalizeHarnessId(unitHarnessId);
        return string.Equals(normalized, CanonicalHarnessId, StringComparison.Ordinal)
            ? CanonicalHarnessId
            : normalized;
    }
}
