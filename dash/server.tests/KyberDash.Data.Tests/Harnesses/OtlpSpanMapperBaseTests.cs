using System.Text;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.Otlp;
using KyberDash.Data.Repositories;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Data.Tests.Harnesses;

/// <summary>
/// Covers the shared <see cref="OtlpSpanMapperBase"/> delegation behind the six OTLP span mappers.
/// Empty units, harness-override passthrough, and canonical harness ids must behave identically
/// for every mapper so the per-mapper wire contracts stay the only differing code.
/// </summary>
public class OtlpSpanMapperBaseTests
{
    private static readonly DateTimeOffset ImportedAtUtc = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static OtlpSpanMapperBase[] AllMappers() =>
    [
        new AntigravitySpanMapper(),
        new ClaudeSpanMapper(),
        new CopilotSpanMapper(),
        new DroidSpanMapper(),
        new OpenCodeSpanMapper(),
        new PiSpanMapper(),
    ];

    private static string CanonicalHarnessId(OtlpSpanMapperBase mapper) =>
        mapper switch
        {
            AntigravitySpanMapper => AntigravitySpanMapper.AntigravityHarnessId,
            ClaudeSpanMapper => TranscriptReaderRepository.ClaudeHarnessId,
            CopilotSpanMapper => CopilotSpanMapper.CopilotHarnessId,
            DroidSpanMapper => DroidSpanMapper.DroidHarnessId,
            OpenCodeSpanMapper => OpenCodeSpanMapper.OpenCodeHarnessId,
            PiSpanMapper => PiSpanMapper.PiHarnessId,
            _ => throw new InvalidOperationException($"Unknown mapper {mapper.GetType().Name}."),
        };

    [Fact]
    public void HarnessId_ReturnsCanonicalId_ForEveryMapper()
    {
        foreach (OtlpSpanMapperBase mapper in AllMappers())
        {
            Assert.Equal(CanonicalHarnessId(mapper), mapper.HarnessId);
        }
    }

    [Fact]
    public void Map_EmptyRawText_ReturnsEmptyBatchWithCanonicalHarness_ForEveryMapper()
    {
        foreach (OtlpSpanMapperBase mapper in AllMappers())
        {
            string canonical = CanonicalHarnessId(mapper);
            SourceUnit descriptor = new(
                HarnessId: canonical,
                SourceKey: "otlp-test-source",
                ProviderId: "test-provider",
                ParserId: "test-otlp-mapper",
                ParserContractVersion: "1",
                Format: "otlp-json",
                SourceRootLabel: "otlp",
                RevisionToken: "rev-1",
                Kind: SourceKind.Otlp);
            NativeSourceUnit unit = new(descriptor, string.Empty, 0, ImportedAtUtc);

            CollectedBatch batch = mapper.Map(unit, ImportedAtUtc);

            Assert.Equal(canonical, batch.Source.HarnessId);
            Assert.Equal(SourceKind.Otlp, batch.Source.Kind);
            Assert.Empty(batch.Records);
            Assert.Empty(batch.Sessions);
            Assert.Empty(batch.Problems);
        }
    }

    [Fact]
    public void MapSpans_HarnessOverride_PassesThroughResolvedId_ForEveryMapper()
    {
        foreach (OtlpSpanMapperBase mapper in AllMappers())
        {
            CollectedBatch @default = mapper.MapSpans([], sourceLocator: "otlp-test");
            Assert.Equal(CanonicalHarnessId(mapper), @default.Source.HarnessId);

            CollectedBatch overridden = mapper.MapSpans([], sourceLocator: "otlp-test", harnessIdOverride: "custom-harness");
            Assert.Equal("custom-harness", overridden.Source.HarnessId);
        }
    }
}
