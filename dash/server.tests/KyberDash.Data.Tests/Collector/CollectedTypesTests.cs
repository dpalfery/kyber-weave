using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberDash.Data.Collector;
using KyberDash.Domain.Schema;
using Xunit;

namespace KyberDash.Data.Tests.Collector;

/// <summary>
/// Reflection, schema-mapping, and constructor-invariant tests for the static collector DTOs
/// in <see cref="KyberDash.Data.Collector"/>.
/// </summary>
public class CollectedTypesTests
{
    private const string CollectorNamespace = "KyberDash.Data.Collector";

    private static readonly Type[] ExpectedCollectorTypes =
    [
        typeof(RecordOp),
        typeof(RecordKind),
        typeof(RecordStatus),
        typeof(TokenClass),
        typeof(SourceKind),
        typeof(ContentBucket),
        typeof(CollectedSource),
        typeof(CollectedBatch),
        typeof(CollectedProblem),
        typeof(CollectedSession),
        typeof(CollectedRecord),
        typeof(CollectedToolCall),
        typeof(CollectedToolContent),
        typeof(CollectedAgentInvocation),
        typeof(CollectedContentPart),
        typeof(CollectedWithheldText),
        typeof(CollectedToolDefinition),
        typeof(CollectedRecordSignals),
        typeof(CollectedModelUsage),
        typeof(CollectedWorkspace),
        typeof(CollectedCost),
        typeof(CollectedError),
        typeof(CollectedTokenCount),
    ];

    [Fact]
    public void CollectorNamespace_ContainsAllExpectedTypes()
    {
        Type[] actualTypes = GetCollectorTypes();

        foreach (Type expected in ExpectedCollectorTypes)
        {
            Assert.Contains(expected, actualTypes);
        }

        Assert.Equal(ExpectedCollectorTypes.Length, actualTypes.Length);
    }

    [Fact]
    public void CollectorDtos_ContainNoJsonElementJsonNodeObjectOrDictionaryProperties()
    {
        Type[] collectorTypes = GetCollectorTypes();
        Assert.NotEmpty(collectorTypes);

        foreach (Type dtoType in collectorTypes)
        {
            if (dtoType.IsEnum)
                continue;

            Assert.False(dtoType.IsSealed, $"DTO '{dtoType.FullName}' must remain unsealed per ArchitectureBoundaryTests.");

            foreach (PropertyInfo property in GetDeclaredDtoProperties(dtoType))
            {
                AssertStaticTypedProperty(dtoType, property.Name, property.PropertyType);
            }
        }
    }

    [Fact]
    public void CollectorEnums_MatchRelationalSchemaCatalogVocabularies()
    {
        AssertEnumNames(typeof(RecordOp), ["LlmInvoke", "ToolInvoke", "AgentInvoke", "Unspecified"]);
        AssertEnumNames(typeof(RecordKind), ["Unspecified", "Internal", "Server", "Client", "Producer", "Consumer"]);
        AssertEnumNames(typeof(RecordStatus), ["Unset", "Ok", "Error", "Unspecified"]);
        AssertEnumNames(typeof(TokenClass), ["Input", "CacheRead", "CacheWrite", "Output", "Reasoning", "Total"]);
        AssertEnumNames(typeof(SourceKind), ["File", "Otlp"]);
        AssertEnumNames(
            typeof(ContentBucket),
            [
                "SystemPrompt",
                "ToolDefinitions",
                "ToolDefinition",
                "InstructionContext",
                "ConversationHistory",
                "ToolResultContent",
                "UserPrompt",
                "UserMessage",
                "Reasoning",
            ]);
    }

    [Fact]
    public void CollectorDtos_MapEveryPropertyToRelationalSchemaCatalogColumnOrNestedDto()
    {
        Dictionary<(Type Type, string Property), PropertyMapping> expectedMappings = BuildExpectedMappings();

        HashSet<(Type Type, string Property)> seenProperties = [];

        foreach (Type dtoType in GetCollectorTypes())
        {
            if (dtoType.IsEnum)
                continue;

            foreach (PropertyInfo property in GetDeclaredDtoProperties(dtoType))
            {
                (Type Type, string Property) key = (dtoType, property.Name);
                Assert.True(seenProperties.Add(key));
                Assert.True(
                    expectedMappings.TryGetValue(key, out PropertyMapping? mapping),
                    $"Property '{dtoType.Name}.{property.Name}' has no RelationalSchemaCatalog or nested DTO mapping.");

                if (mapping.NestedDtoType is not null)
                {
                    Type expectedClrType = mapping.IsCollection
                        ? typeof(IReadOnlyList<>).MakeGenericType(mapping.NestedDtoType)
                        : mapping.NestedDtoType;
                    Assert.Equal(expectedClrType, property.PropertyType);
                }
                else
                {
                    Assert.NotEmpty(mapping.Columns);
                    foreach ((string tableName, string columnName) in mapping.Columns)
                    {
                        TableDefinition table = RelationalSchemaCatalog.GetTable(tableName);
                        Assert.Contains(table.Columns, column => string.Equals(column.Name, columnName, StringComparison.Ordinal));
                    }
                }
            }
        }

        foreach ((Type Type, string Property) mappedKey in expectedMappings.Keys)
        {
            Assert.Contains(mappedKey, seenProperties);
        }
    }

    [Fact]
    public void CollectedContentPart_ThrowsArgumentException_WhenBothPlainTextAndWithheldAreNull()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            new CollectedContentPart(
                Position: 0,
                Bucket: ContentBucket.SystemPrompt,
                PlainText: null,
                Withheld: null,
                ReportedTokens: 12,
                Server: null,
                Role: "system",
                Resident: true,
                OriginalUtf8BytesWhenTruncated: null));

        Assert.Contains("PlainText", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CollectedContentPart_ThrowsArgumentException_WhenBothPlainTextAndWithheldAreProvided()
    {
        CollectedWithheldText withheld = new(Utf8Bytes: 100, Characters: 90, MeasuredTokens: 25);

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            new CollectedContentPart(
                Position: 1,
                Bucket: ContentBucket.UserPrompt,
                PlainText: "hello",
                Withheld: withheld,
                ReportedTokens: null,
                Server: null,
                Role: "user",
                Resident: null,
                OriginalUtf8BytesWhenTruncated: null));

        Assert.Contains("Withheld", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("system instructions")]
    public void CollectedContentPart_AcceptsPlainTextWithoutWithheld(string plainText)
    {
        CollectedContentPart part = new(
            Position: 0,
            Bucket: ContentBucket.InstructionContext,
            PlainText: plainText,
            Withheld: null,
            ReportedTokens: 5,
            Server: "context-server",
            Role: "system",
            Resident: true,
            OriginalUtf8BytesWhenTruncated: 256);

        Assert.Equal(plainText, part.PlainText);
        Assert.Null(part.Withheld);
        Assert.Equal(0, part.Position);
        Assert.Equal(ContentBucket.InstructionContext, part.Bucket);
        Assert.Equal(5, part.ReportedTokens);
        Assert.Equal("context-server", part.Server);
        Assert.Equal("system", part.Role);
        Assert.True(part.Resident);
        Assert.Equal(256, part.OriginalUtf8BytesWhenTruncated);
    }

    [Fact]
    public void CollectedContentPart_AcceptsWithheldWithoutPlainText()
    {
        CollectedWithheldText withheld = new(Utf8Bytes: 42, Characters: 40, MeasuredTokens: 11);

        CollectedContentPart part = new(
            Position: 2,
            Bucket: ContentBucket.ToolResultContent,
            PlainText: null,
            Withheld: withheld,
            ReportedTokens: null,
            Server: null,
            Role: "tool",
            Resident: false,
            OriginalUtf8BytesWhenTruncated: null);

        Assert.Null(part.PlainText);
        Assert.Equal(withheld, part.Withheld);
        Assert.Equal(2, part.Position);
        Assert.Equal(ContentBucket.ToolResultContent, part.Bucket);
    }

    [Fact]
    public void CollectedProblem_DefaultsOptionalFieldsToNull()
    {
        CollectedProblem problem = new(
            ProblemKey: "prob-1",
            Severity: "error",
            Code: "PARSE_ERR",
            Message: "Malformed span");

        Assert.Null(problem.SpanId);
        Assert.Null(problem.Location);
        Assert.Null(problem.SessionKey);
        Assert.Null(problem.HarnessId);
        Assert.Null(problem.TimestampUtc);
        Assert.Null(problem.Stack);
    }

    [Fact]
    public void CollectedRecord_DefaultsReportedTotalsToNull()
    {
        DateTimeOffset startedAt = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);

        CollectedRecord record = new(
            RecordId: "span-1",
            HarnessId: "claude-code",
            Op: RecordOp.LlmInvoke,
            Kind: RecordKind.Client,
            Name: "claude.message",
            StartedAt: startedAt,
            Status: RecordStatus.Ok,
            SessionKey: null,
            ParentRecordId: null,
            TraceId: null,
            Model: "claude-sonnet-4-5",
            EndedAt: null,
            ReportedDurationMs: null,
            Tokens: [],
            ReportedCost: null,
            Parts: [],
            Tool: null,
            Agent: null,
            SkillsInvoked: [],
            AgentInstanceId: null,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(null, null, null, null));

        Assert.Null(record.ReportedInputTokens);
        Assert.Null(record.ReportedOutputTokens);
    }

    private static Type[] GetCollectorTypes() =>
        typeof(CollectedBatch).Assembly
            .GetTypes()
            .Where(static type =>
                string.Equals(type.Namespace, CollectorNamespace, StringComparison.Ordinal)
                && !type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
                && !type.Name.Contains('<', StringComparison.Ordinal))
            .ToArray();

    private static IEnumerable<PropertyInfo> GetDeclaredDtoProperties(Type dtoType) =>
        dtoType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(static property => !string.Equals(property.Name, "EqualityContract", StringComparison.Ordinal));

    private static void AssertEnumNames(Type enumType, string[] expectedNames)
    {
        Assert.True(enumType.IsEnum, $"Expected '{enumType.Name}' to be an enum.");
        Assert.Equal(expectedNames, Enum.GetNames(enumType));
    }

    private static void AssertStaticTypedProperty(Type ownerType, string propertyName, Type propertyType)
    {
        Assert.False(
            propertyType == typeof(object),
            $"Property '{ownerType.Name}.{propertyName}' must not be typed as object.");

        Assert.False(
            propertyType == typeof(JsonElement) || propertyType == typeof(JsonDocument),
            $"Property '{ownerType.Name}.{propertyName}' must not use {propertyType.Name}.");

        Assert.False(
            typeof(JsonNode).IsAssignableFrom(propertyType),
            $"Property '{ownerType.Name}.{propertyName}' must not use JsonNode ({propertyType.Name}).");

        Assert.False(
            IsDictionaryType(propertyType),
            $"Property '{ownerType.Name}.{propertyName}' must not be a dictionary ({propertyType.Name}).");

        if (propertyType.IsArray)
        {
            Type? elementType = propertyType.GetElementType();
            if (elementType is not null)
                AssertStaticTypedProperty(ownerType, propertyName, elementType);
            return;
        }

        if (propertyType.IsGenericType)
        {
            foreach (Type genericArgument in propertyType.GetGenericArguments())
            {
                AssertStaticTypedProperty(ownerType, propertyName, genericArgument);
            }
        }
    }

    private static bool IsDictionaryType(Type type)
    {
        if (typeof(IDictionary).IsAssignableFrom(type))
            return true;

        if (type.IsGenericType)
        {
            Type genericDefinition = type.GetGenericTypeDefinition();
            if (genericDefinition == typeof(IDictionary<,>) || genericDefinition == typeof(IReadOnlyDictionary<,>))
                return true;
        }

        return type.GetInterfaces().Any(static iface =>
            iface.IsGenericType
            && (iface.GetGenericTypeDefinition() == typeof(IDictionary<,>)
                || iface.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));
    }

    private static Dictionary<(Type Type, string Property), PropertyMapping> BuildExpectedMappings()
    {
        Dictionary<(Type Type, string Property), PropertyMapping> map = [];

        static PropertyMapping Col(string table, string column) => new(null, false, [(table, column)]);
        static PropertyMapping Cols(params (string Table, string Column)[] columns) => new(null, false, columns);
        static PropertyMapping Nested(Type dtoType) => new(dtoType, false, []);
        static PropertyMapping NestedList(Type dtoType) => new(dtoType, true, []);

        map[(typeof(CollectedSource), nameof(CollectedSource.HarnessId))] = Col("source", "harness_id");
        map[(typeof(CollectedSource), nameof(CollectedSource.Kind))] = Col("source", "source_kind");
        map[(typeof(CollectedSource), nameof(CollectedSource.Locator))] = Col("source", "locator");

        map[(typeof(CollectedBatch), nameof(CollectedBatch.Source))] = Nested(typeof(CollectedSource));
        map[(typeof(CollectedBatch), nameof(CollectedBatch.Sessions))] = NestedList(typeof(CollectedSession));
        map[(typeof(CollectedBatch), nameof(CollectedBatch.Records))] = NestedList(typeof(CollectedRecord));
        map[(typeof(CollectedBatch), nameof(CollectedBatch.Problems))] = NestedList(typeof(CollectedProblem));

        map[(typeof(CollectedProblem), nameof(CollectedProblem.ProblemKey))] = Col("ingest_problem", "problem_key");
        map[(typeof(CollectedProblem), nameof(CollectedProblem.Severity))] = Col("ingest_problem", "severity");
        map[(typeof(CollectedProblem), nameof(CollectedProblem.Code))] = Col("ingest_problem", "code");
        map[(typeof(CollectedProblem), nameof(CollectedProblem.Message))] = Col("ingest_problem", "message");
        map[(typeof(CollectedProblem), nameof(CollectedProblem.SpanId))] = Col("ingest_problem_span", "span_id");
        map[(typeof(CollectedProblem), nameof(CollectedProblem.Location))] = Col("ingest_problem_location", "location");
        map[(typeof(CollectedProblem), nameof(CollectedProblem.SessionKey))] = Col("ingest_problem_session", "session_id");
        map[(typeof(CollectedProblem), nameof(CollectedProblem.HarnessId))] = Col("ingest_problem_harness", "harness_id");
        map[(typeof(CollectedProblem), nameof(CollectedProblem.TimestampUtc))] = Col("ingest_problem_timestamp", "timestamp_utc");
        map[(typeof(CollectedProblem), nameof(CollectedProblem.Stack))] = Col("ingest_problem_stack", "stack");

        map[(typeof(CollectedSession), nameof(CollectedSession.SessionKey))] = Col("session", "session_id");
        map[(typeof(CollectedSession), nameof(CollectedSession.HarnessId))] = Col("session", "harness_id");
        map[(typeof(CollectedSession), nameof(CollectedSession.NativeSessionId))] = Col("session", "native_session_id");
        map[(typeof(CollectedSession), nameof(CollectedSession.Workspace))] = Nested(typeof(CollectedWorkspace));
        map[(typeof(CollectedSession), nameof(CollectedSession.Title))] = Col("session_title", "title");
        map[(typeof(CollectedSession), nameof(CollectedSession.ReportedUsage))] = NestedList(typeof(CollectedModelUsage));

        map[(typeof(CollectedWorkspace), nameof(CollectedWorkspace.WorkingDirectory))] = Col("session_workspace", "working_directory");
        map[(typeof(CollectedWorkspace), nameof(CollectedWorkspace.Repository))] = Col("session_repository", "repo_name");
        map[(typeof(CollectedWorkspace), nameof(CollectedWorkspace.Branch))] = Col("session_branch", "branch_name");

        map[(typeof(CollectedModelUsage), nameof(CollectedModelUsage.SnapshotPosition))] = Col("session_model_usage", "snapshot_position");
        map[(typeof(CollectedModelUsage), nameof(CollectedModelUsage.Model))] = Col("session_model_usage", "model_id");
        map[(typeof(CollectedModelUsage), nameof(CollectedModelUsage.TokenClass))] = Col("session_model_usage", "token_class");
        map[(typeof(CollectedModelUsage), nameof(CollectedModelUsage.Count))] = Col("session_model_usage", "token_count");

        map[(typeof(CollectedTokenCount), nameof(CollectedTokenCount.TokenClass))] = Col("record_token_count", "token_class");
        map[(typeof(CollectedTokenCount), nameof(CollectedTokenCount.Count))] = Col("record_token_count", "token_count");

        map[(typeof(CollectedCost), nameof(CollectedCost.Amount))] = Col("record_reported_cost", "amount");
        map[(typeof(CollectedCost), nameof(CollectedCost.Currency))] = Col("record_reported_cost", "currency");

        map[(typeof(CollectedError), nameof(CollectedError.Message))] = Col("tool_call_error", "message");
        map[(typeof(CollectedError), nameof(CollectedError.Code))] = Col("tool_call_error_code", "code");

        map[(typeof(CollectedWithheldText), nameof(CollectedWithheldText.Utf8Bytes))] = Col("content_part_withheld", "utf8_bytes");
        map[(typeof(CollectedWithheldText), nameof(CollectedWithheldText.Characters))] = Col("content_part_withheld", "characters");
        map[(typeof(CollectedWithheldText), nameof(CollectedWithheldText.MeasuredTokens))] = Col("content_part_withheld", "measured_tokens");

        map[(typeof(CollectedContentPart), nameof(CollectedContentPart.Position))] = Col("content_part", "position");
        map[(typeof(CollectedContentPart), nameof(CollectedContentPart.Bucket))] = Col("content_part", "bucket");
        map[(typeof(CollectedContentPart), nameof(CollectedContentPart.PlainText))] = Col("content_part_text", "part_text");
        map[(typeof(CollectedContentPart), nameof(CollectedContentPart.Withheld))] = Nested(typeof(CollectedWithheldText));
        map[(typeof(CollectedContentPart), nameof(CollectedContentPart.ReportedTokens))] = Col("content_part_reported_tokens", "token_count");
        map[(typeof(CollectedContentPart), nameof(CollectedContentPart.Server))] = Col("content_part_server", "server_name");
        map[(typeof(CollectedContentPart), nameof(CollectedContentPart.Role))] = Col("content_part_role", "role_name");
        map[(typeof(CollectedContentPart), nameof(CollectedContentPart.Resident))] = Col("content_part_residency", "is_resident");
        map[(typeof(CollectedContentPart), nameof(CollectedContentPart.OriginalUtf8BytesWhenTruncated))] = Col("content_part_truncation", "original_utf8_bytes");

        map[(typeof(CollectedToolDefinition), nameof(CollectedToolDefinition.Position))] = Col("tool_definition", "position");
        map[(typeof(CollectedToolDefinition), nameof(CollectedToolDefinition.ToolName))] = Col("tool_definition", "tool_name");
        map[(typeof(CollectedToolDefinition), nameof(CollectedToolDefinition.Server))] = Col("tool_definition_server", "server_name");
        map[(typeof(CollectedToolDefinition), nameof(CollectedToolDefinition.SchemaTokens))] = Col("tool_definition", "schema_tokens");

        map[(typeof(CollectedToolContent), nameof(CollectedToolContent.PlainText))] = Cols(
            ("tool_call_input_text", "text"),
            ("tool_call_result_text", "text"));
        map[(typeof(CollectedToolContent), nameof(CollectedToolContent.Utf8Bytes))] = Cols(
            ("tool_call_input_size", "utf8_bytes"),
            ("tool_call_result_size", "utf8_bytes"));
        map[(typeof(CollectedToolContent), nameof(CollectedToolContent.Characters))] = Cols(
            ("tool_call_input_size", "characters"),
            ("tool_call_result_size", "characters"));
        map[(typeof(CollectedToolContent), nameof(CollectedToolContent.Sha256Fingerprint))] = Col("tool_call_input_fingerprint", "sha256");
        map[(typeof(CollectedToolContent), nameof(CollectedToolContent.OriginalUtf8BytesWhenTruncated))] = Cols(
            ("tool_call_input_truncation", "original_utf8_bytes"),
            ("tool_call_result_truncation", "original_utf8_bytes"));

        map[(typeof(CollectedToolCall), nameof(CollectedToolCall.CallId))] = Col("tool_call", "call_id");
        map[(typeof(CollectedToolCall), nameof(CollectedToolCall.ToolName))] = Col("tool_call", "tool_name");
        map[(typeof(CollectedToolCall), nameof(CollectedToolCall.McpServerName))] = Col("tool_call_mcp", "server_name");
        map[(typeof(CollectedToolCall), nameof(CollectedToolCall.McpToolName))] = Col("tool_call_mcp", "server_tool_name");
        map[(typeof(CollectedToolCall), nameof(CollectedToolCall.Input))] = Nested(typeof(CollectedToolContent));
        map[(typeof(CollectedToolCall), nameof(CollectedToolCall.Result))] = Nested(typeof(CollectedToolContent));
        map[(typeof(CollectedToolCall), nameof(CollectedToolCall.Succeeded))] = Col("tool_call_outcome", "succeeded");
        map[(typeof(CollectedToolCall), nameof(CollectedToolCall.Error))] = Nested(typeof(CollectedError));
        map[(typeof(CollectedToolCall), nameof(CollectedToolCall.ExitCode))] = Col("tool_call_exit_code", "exit_code");

        map[(typeof(CollectedAgentInvocation), nameof(CollectedAgentInvocation.AgentName))] = Col("agent_invocation", "agent_name");
        map[(typeof(CollectedAgentInvocation), nameof(CollectedAgentInvocation.SpawningCallId))] = Col("agent_invocation_spawn", "spawning_record_id");
        map[(typeof(CollectedAgentInvocation), nameof(CollectedAgentInvocation.Succeeded))] = Col("agent_invocation_outcome", "succeeded");
        map[(typeof(CollectedAgentInvocation), nameof(CollectedAgentInvocation.Error))] = Col("agent_invocation_error", "message");

        map[(typeof(CollectedRecordSignals), nameof(CollectedRecordSignals.ErrorMessage))] = Col("record_error", "message");
        map[(typeof(CollectedRecordSignals), nameof(CollectedRecordSignals.EditFailures))] = Col("record_edit_failure", "failure_count");
        map[(typeof(CollectedRecordSignals), nameof(CollectedRecordSignals.CorrectionRule))] = Col("record_correction_rule", "correction_rule");
        map[(typeof(CollectedRecordSignals), nameof(CollectedRecordSignals.DeclaredContextWindow))] = Col("record_declared_context_window", "declared_context_window");

        map[(typeof(CollectedRecord), nameof(CollectedRecord.RecordId))] = Col("record", "span_id");
        map[(typeof(CollectedRecord), nameof(CollectedRecord.HarnessId))] = Col("record", "harness_id");
        map[(typeof(CollectedRecord), nameof(CollectedRecord.Op))] = Col("record", "op");
        map[(typeof(CollectedRecord), nameof(CollectedRecord.Kind))] = Col("record", "kind");
        map[(typeof(CollectedRecord), nameof(CollectedRecord.Name))] = Col("record", "name");
        map[(typeof(CollectedRecord), nameof(CollectedRecord.StartedAt))] = Col("record", "started_at_utc");
        map[(typeof(CollectedRecord), nameof(CollectedRecord.Status))] = Col("record", "status");
        map[(typeof(CollectedRecord), nameof(CollectedRecord.SessionKey))] = Col("record_session_link", "session_key");
        map[(typeof(CollectedRecord), nameof(CollectedRecord.ParentRecordId))] = Col("record_parent", "parent_span_id");
        map[(typeof(CollectedRecord), nameof(CollectedRecord.TraceId))] = Col("record_trace", "trace_id");
        map[(typeof(CollectedRecord), nameof(CollectedRecord.Model))] = Col("record_model", "model_id");
        map[(typeof(CollectedRecord), nameof(CollectedRecord.EndedAt))] = Col("record_end", "ended_at_utc");
        map[(typeof(CollectedRecord), nameof(CollectedRecord.ReportedDurationMs))] = Col("record_reported_duration", "duration_ms");
        map[(typeof(CollectedRecord), nameof(CollectedRecord.Tokens))] = NestedList(typeof(CollectedTokenCount));
        map[(typeof(CollectedRecord), nameof(CollectedRecord.ReportedCost))] = Nested(typeof(CollectedCost));
        map[(typeof(CollectedRecord), nameof(CollectedRecord.Parts))] = NestedList(typeof(CollectedContentPart));
        map[(typeof(CollectedRecord), nameof(CollectedRecord.Tool))] = Nested(typeof(CollectedToolCall));
        map[(typeof(CollectedRecord), nameof(CollectedRecord.Agent))] = Nested(typeof(CollectedAgentInvocation));
        map[(typeof(CollectedRecord), nameof(CollectedRecord.SkillsInvoked))] = Col("record_skill_invocation", "skill_name");
        map[(typeof(CollectedRecord), nameof(CollectedRecord.AgentInstanceId))] = Col("record_agent_instance", "agent_instance_id");
        map[(typeof(CollectedRecord), nameof(CollectedRecord.ToolDefinitions))] = NestedList(typeof(CollectedToolDefinition));
        map[(typeof(CollectedRecord), nameof(CollectedRecord.Signals))] = Nested(typeof(CollectedRecordSignals));
        map[(typeof(CollectedRecord), nameof(CollectedRecord.ReportedInputTokens))] = Col("record_reported_total", "token_count");
        map[(typeof(CollectedRecord), nameof(CollectedRecord.ReportedOutputTokens))] = Col("record_reported_total", "token_count");
        map[(typeof(CollectedRecord), nameof(CollectedRecord.SourceName))] = Col("source", "locator");

        return map;
    }

    private sealed record PropertyMapping(
        Type? NestedDtoType,
        bool IsCollection,
        IReadOnlyList<(string TableName, string ColumnName)> Columns);
}
