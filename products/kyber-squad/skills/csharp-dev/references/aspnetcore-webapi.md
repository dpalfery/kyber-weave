---
name: csharp-dev/aspnetcore-webapi
description: ASP.NET Core Web API patterns — sealed record DTOs, TypedResults, RFC 7807 problem details, OpenAPI with Swashbuckle, controller checklist.
source: https://github.com/dotnet/skills/tree/main/plugins/aspnetcore/skills/dotnet-webapi
---

# ASP.NET Core Web API

## DTOs — Sealed Records

DTO shape — `sealed record`, compiler-generated equality — follows **<csharp-coding-standard>** § Types.

```csharp
public sealed record CreateManualRequest(
    string Title,
    string Make,
    string Model,
    int Year);

public sealed record ManualResponse(
    Guid Id,
    string Title,
    string Make,
    string Model,
    int Year,
    DateTimeOffset CreatedAt);
```

---

## TypedResults (Preferred over `Ok()` / `NotFound()`)

`TypedResults` usage follows **<csharp-coding-standard>** § Stack:

```csharp
[HttpGet("{id:guid}")]
[ProducesResponseType<ManualResponse>(StatusCodes.Status200OK)]
[ProducesResponseType(StatusCodes.Status404NotFound)]
public async Task<Results<Ok<ManualResponse>, NotFound>> GetAsync(Guid id)
{
    Manual? manual = await _service.GetByIdAsync(id);
    if (manual is null)
        return TypedResults.NotFound();

    return TypedResults.Ok(new ManualResponse(
        manual.Id, manual.Title, manual.Make, manual.Model, manual.Year, manual.CreatedAt));
}
```

---

## RFC 7807 Problem Details

Structured-error handling follows **<csharp-coding-standard>** § Errors:

```csharp
[HttpPost]
public async Task<Results<Created<ManualResponse>, ValidationProblem>> CreateAsync(
    CreateManualRequest request,
    CancellationToken cancellationToken = default)
{
    Dictionary<string, string[]> validationErrors = Validate(request);
    if (validationErrors.Count > 0)
        return TypedResults.ValidationProblem(validationErrors);

    Manual manual = await _service.CreateAsync(request, cancellationToken);
    return TypedResults.Created($"/api/manuals/{manual.Id}", Map(manual));
}
```

Register the problem details service in `Program.cs`:

```csharp
builder.Services.AddProblemDetails();
```

For unhandled exceptions, use the built-in exception handler:

```csharp
app.UseExceptionHandler();  // produces RFC 7807 JSON automatically
```

---

## OpenAPI and Swashbuckle

Emit the OpenAPI document with `Microsoft.AspNetCore.OpenApi`. Serve the UI with Swashbuckle.

```csharp
builder.Services.AddOpenApi();
builder.Services.AddSwaggerGen();

app.MapOpenApi();
app.UseSwagger();
app.UseSwaggerUI();
```

Enrich the document:

```csharp
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((doc, ctx, ct) =>
    {
        doc.Info.Title = "API";
        doc.Info.Version = "v1";
        return Task.CompletedTask;
    });
});
```

---

## Controller Checklist

Before shipping a controller action, verify:

- [ ] DTO shape, collection/empty-list handling, cancellation-token propagation, `HttpContext`
      access, and error-response format follow **<csharp-coding-standard>**
- [ ] `[ProducesResponseType]` attributes match the actual `TypedResults` return types
- [ ] No business logic in controller — delegate to Application service

---

## Routing Conventions

```csharp
[ApiController]
[Route("api/[controller]")]   // → /api/manuals
public class ManualsController : ControllerBase
{
    [HttpGet]                  // GET /api/manuals
    [HttpGet("{id:guid}")]     // GET /api/manuals/{guid}
    [HttpPost]                 // POST /api/manuals
    [HttpPut("{id:guid}")]     // PUT /api/manuals/{guid}
    [HttpDelete("{id:guid}")]  // DELETE /api/manuals/{guid}
}
```

Route-constraint usage (`:guid`, `:int`, `:alpha`) follows **<csharp-coding-standard>** § HTTP pipeline.

---

## Cancellation Token

Cancellation-token propagation on async actions follows **<csharp-coding-standard>** § Async:

```csharp
[HttpGet]
public async Task<Ok<IEnumerable<ManualResponse>>> GetAllAsync(
    CancellationToken cancellationToken)
{
    IEnumerable<Manual> manuals = await _service.GetAllAsync(cancellationToken);
    return TypedResults.Ok(manuals.Select(Map));
}
```

---

## References

- [Controller action return types](https://learn.microsoft.com/aspnet/core/web-api/action-return-types)
- [TypedResults](https://learn.microsoft.com/aspnet/core/fundamentals/minimal-apis/responses#typedresults-vs-results)
- [Problem details](https://learn.microsoft.com/aspnet/core/web-api/handle-errors#problem-details)
- [ASP.NET Core OpenAPI (.NET 9+)](https://learn.microsoft.com/aspnet/core/fundamentals/openapi/overview)
