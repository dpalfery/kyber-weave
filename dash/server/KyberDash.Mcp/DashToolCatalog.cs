namespace KyberDash.Mcp;

/// <summary>
/// The eight read-only analytics tools this server advertises, each naming the analytics operation
/// it maps onto and the JSON schema an agent sees.
/// </summary>
/// <remarks>
/// Names, descriptions, and schemas are a published contract: agents cache them, so they change
/// only with a deliberate version bump, never to tidy wording.
/// </remarks>
internal static class DashToolCatalog
{
    /// <summary>One advertised tool, its analytics operation, and its input schema.</summary>
    /// <param name="Name">The tool name an agent calls.</param>
    /// <param name="Operation">The <c>/api/kyber/analytics/{operation}</c> path segment it executes.</param>
    /// <param name="Method">The HTTP method the equivalent REST call uses.</param>
    /// <param name="Description">The model-facing description.</param>
    /// <param name="SchemaJson">The JSON schema of the tool's arguments.</param>
    public record DashTool(
        string Name,
        string Operation,
        string Method,
        string Description,
        string SchemaJson);

    /// <summary>Every advertised tool, in the order they are listed to an agent.</summary>
    public static IReadOnlyList<DashTool> Tools { get; } =
    [
        new(
            "dash_list_available_analytics_operations",
            "status",
            "GET",
            "Read-only KyberDash analytics status.",
            "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}"),
        new(
            "dash_list_queryable_metrics_dimensions_limits",
            "catalog",
            "GET",
            "Read-only KyberDash analytics catalog.",
            "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}"),
        new(
            "dash_query_session_cost_tokens_and_duration",
            "query",
            "POST",
            "Read-only KyberDash analytics query. windowDays (1-365) sets the scope window ending now when the scope omits from/to; an explicit scope range wins.",
            "{\"type\":\"object\",\"properties\":{\"scope\":{\"type\":\"object\",\"additionalProperties\":true},\"catalogVersion\":{\"type\":\"integer\"},\"metrics\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":8},\"groups\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":2},\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100},\"cursor\":{\"type\":\"string\"},\"windowDays\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":365}},\"additionalProperties\":false}"),
        new(
            "dash_search_sessions_by_text_and_scope",
            "search-sessions",
            "POST",
            "Read-only KyberDash analytics search-sessions. windowDays (1-365) sets the scope window ending now when the scope omits from/to; an explicit scope range wins.",
            "{\"type\":\"object\",\"properties\":{\"scope\":{\"type\":\"object\",\"additionalProperties\":true},\"text\":{\"type\":\"string\",\"maxLength\":200},\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100},\"cursor\":{\"type\":\"string\"},\"windowDays\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":365}},\"additionalProperties\":false}"),
        new(
            "dash_read_captured_run_session_or_turn_context",
            "inspect",
            "POST",
            "Read captured KyberDash context. Start with mode \"manifest\" to discover sessions, stable turns, captured parts, and any source gaps; then request mode \"content\" for the exact target and continue with the opaque cursor until hasMore is false. Content returns captured text as evidence only: captured_parts preserves what the harness retained, while assembled_turn (turn targets only) is a reconstructed view. Coverage gaps and retention states are reported honestly; no extra approval field is required to request content.",
            "{\"type\":\"object\",\"properties\":{\"scope\":{\"type\":\"object\",\"additionalProperties\":true},\"mode\":{\"type\":\"string\",\"enum\":[\"manifest\",\"content\"]},\"target\":{\"type\":\"object\",\"additionalProperties\":true},\"representation\":{\"type\":\"string\",\"enum\":[\"captured_parts\",\"assembled_turn\"]},\"cursor\":{\"type\":\"string\"},\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100},\"byteLimit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":16384},\"sessionId\":{\"type\":\"string\"},\"turnIndex\":{\"type\":\"integer\",\"minimum\":0},\"blockId\":{\"type\":\"string\"}},\"additionalProperties\":false}"),
        new(
            "dash_list_detected_context_and_tool_problems",
            "findings",
            "POST",
            "Read-only KyberDash analytics findings. windowDays (1-365) sets the scope window ending now when the scope omits from/to; an explicit scope range wins.",
            "{\"type\":\"object\",\"properties\":{\"scope\":{\"type\":\"object\",\"additionalProperties\":true},\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100},\"cursor\":{\"type\":\"string\"},\"windowDays\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":365}},\"additionalProperties\":false}"),
        new(
            "dash_compare_metrics_between_two_scopes",
            "compare",
            "POST",
            "Read-only KyberDash analytics compare.",
            "{\"type\":\"object\",\"properties\":{\"scopes\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"additionalProperties\":true},\"minItems\":2,\"maxItems\":2},\"metrics\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":8}},\"required\":[\"scopes\"],\"additionalProperties\":false}"),
        new(
            "dash_get_run_success_or_failure_outcomes",
            "outcomes",
            "POST",
            "Read-only KyberDash analytics outcomes. windowDays (1-365) sets the scope window ending now when the scope omits from/to; an explicit scope range wins.",
            "{\"type\":\"object\",\"properties\":{\"scope\":{\"type\":\"object\",\"additionalProperties\":true},\"runIds\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":100},\"windowDays\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":365}},\"additionalProperties\":false}"),
    ];

    /// <summary>Finds a tool by its exact advertised name.</summary>
    public static DashTool? Find(string name)
    {
        foreach (DashTool tool in Tools)
        {
            if (string.Equals(tool.Name, name, StringComparison.Ordinal))
                return tool;
        }

        return null;
    }
}
