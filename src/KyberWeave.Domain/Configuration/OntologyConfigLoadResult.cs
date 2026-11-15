namespace KyberWeave.Core.Configuration;

/// <summary>Outcome of attempting to load an ontology config file.</summary>
public sealed class OntologyConfigLoadResult
{
    public bool Success { get; private init; }

    public string? ParseError { get; private init; }

    public OntologyConfig? Config { get; private init; }

    public static OntologyConfigLoadResult Ok(OntologyConfig config) =>
        new() { Success = true, Config = config };

    public static OntologyConfigLoadResult Fail(string parseError) =>
        new() { Success = false, ParseError = parseError };
}
