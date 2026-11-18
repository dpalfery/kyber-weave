using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Data.Host;

namespace KyberWeave.Tests;

/// <summary>
/// The standards templates this build ships, read through the real embedded-resource adapter.
/// </summary>
/// <remarks>
/// A content test asserts what a host actually receives, so it reads the shipped templates. A test of a class's own
/// behaviour takes <see cref="Fakes.FakeEmbeddedResources"/> through <see cref="KyberStandardsTemplateReader"/>
/// instead, and names the template text it expects.
/// </remarks>
internal static class ShippedKyberStandards
{
    internal static readonly IKyberStandardsTemplates Templates =
        new KyberStandardsTemplateReader(new AssemblyEmbeddedResources(typeof(KyberStandardsTemplates).Assembly));
}
