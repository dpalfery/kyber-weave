---
name: dotnet-dev/opentelemetry
description: OpenTelemetry in .NET — ActivitySource naming, log-trace correlation, high-cardinality pitfalls, OTLP export.
source: https://github.com/dotnet/skills/tree/main/plugins/aspnetcore/skills/configuring-opentelemetry-dotnet
---

# OpenTelemetry — .NET

## Packages

```xml
<PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.*" />
<PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.*" />
<PackageReference Include="OpenTelemetry.Instrumentation.Http" Version="1.*" />
<PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.*" />
```

---

## Setup in Program.cs

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .SetResourceBuilder(ResourceBuilder.CreateDefault()
            .AddService("MotorcycleRAG.API"))
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddSource("MotorcycleRAG.*")      // register custom ActivitySources
        .AddOtlpExporter(opts =>
        {
            opts.Endpoint = new Uri(builder.Configuration["Otel:Endpoint"]!);
        }))
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter())
    .WithLogging(logging => logging
        .AddOtlpExporter());
```

**Never hardcode the OTLP endpoint or API key** — read from environment/configuration.

---

## Custom ActivitySource

Register the `ActivitySource` name with `AddSource`. `AddSource` matching is case-insensitive and supports wildcard patterns (e.g., `"MotorcycleRAG.*"` or `"*"`):

```csharp
// Define once — typically as a static on the service class
public static class Telemetry
{
    public static readonly ActivitySource Source =
        new("MotorcycleRAG.Application", "1.0.0");
}

// Usage
using var activity = Telemetry.Source.StartActivity("SearchManuals");
activity?.SetTag("query.text", searchQuery);
activity?.SetTag("results.count", results.Count);
```

Ensure `AddSource` includes the `ActivitySource` name or a matching wildcard pattern (such as `"MotorcycleRAG.*"`). Matching is case-insensitive, but maintaining consistent naming across your codebase is recommended.

---

## Log-Trace Correlation

When both logging and tracing are wired to the same OTLP exporter, .NET automatically injects `TraceId` and `SpanId` into log records. To surface them in your log output:

```json
// appsettings.json — add to the log format
{
  "Logging": {
    "Console": {
      "FormatterName": "json",
      "FormatterOptions": {
        "IncludeScopes": true,
        "TimestampFormat": "HH:mm:ss "
      }
    }
  }
}
```

In structured log backends (Application Insights, Grafana Loki), filter by `TraceId` to see all logs for a single request.

---

## Activity Tags vs. Attributes

Use tags for searchable metadata. Follow OpenTelemetry semantic conventions for well-known fields:

```csharp
activity?.SetTag("http.method", "GET");              // OTel semantic convention
activity?.SetTag("db.system", "mssql");
activity?.SetTag("user.id", userId.ToString());      // custom tag
```

**Do NOT add high-cardinality values as tags** — tag values are indexed by every backend. Use `AddEvent` for values that vary per request:

```csharp
// HIGH CARDINALITY — do NOT do this
activity?.SetTag("request.body", requestBody);  // unique per request

// CORRECT — use an Event for the data
activity?.AddEvent(new ActivityEvent("request.received",
    tags: new ActivityTagsCollection { { "body.length", requestBody.Length } }));
```

---

## Sampling

For production, use probabilistic or parent-based sampling to reduce export volume:

```csharp
.WithTracing(tracing => tracing
    // Record 10% of traces; always record if parent sampled
    .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(0.1)))
    ...
)
```

During development use `AlwaysOnSampler` (default) to see all traces.

---

## Azure Monitor Integration

For Application Insights / Azure Monitor instead of OTLP:

```xml
<PackageReference Include="Azure.Monitor.OpenTelemetry.AspNetCore" Version="1.*" />
```

```csharp
builder.Services.AddOpenTelemetry()
    .UseAzureMonitor(options =>
    {
        // Read from environment — never hardcode
        options.ConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
    });
```

**Never hardcode the Application Insights connection string or instrumentation key.** Use `APPLICATIONINSIGHTS_CONNECTION_STRING` environment variable (read automatically by the SDK if not explicitly set).

---

## Health Check

Confirm traces are flowing:

```csharp
// Temporary diagnostic — remove before shipping
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        ...
        .AddConsoleExporter());  // log spans to stdout
```

If no spans appear, check:
1. `AddSource` matches `ActivitySource` name (matching is case-insensitive and supports wildcards)
2. OTLP endpoint is reachable (`curl <endpoint>/v1/traces`)
3. Sampling is not filtering them out (`AlwaysOnSampler` for verification)

---

## References

- [OpenTelemetry .NET getting started](https://opentelemetry.io/docs/instrumentation/net/getting-started/)
- [ActivitySource API](https://learn.microsoft.com/dotnet/core/diagnostics/distributed-tracing-instrumentation-walkthroughs)
- [OTel semantic conventions](https://opentelemetry.io/docs/concepts/semantic-conventions/)
- [Azure Monitor OTel distro](https://learn.microsoft.com/azure/azure-monitor/app/opentelemetry-enable)
