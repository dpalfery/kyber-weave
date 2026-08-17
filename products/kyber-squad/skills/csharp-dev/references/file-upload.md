---
name: dotnet-dev/file-upload
description: ASP.NET Core file upload — dual size limits, magic byte validation, anti-forgery, Guid filenames, IFormFile handling.
source: https://github.com/dotnet/skills/tree/main/plugins/aspnetcore/skills/minimal-api-file-upload
---

# File Upload — ASP.NET Core

## Size Limit Configuration (Must Set Both)

ASP.NET Core has **two independent size limits** that must both be raised for large uploads. Setting only one is the most common mistake.

```csharp
// Program.cs

// 1. Kestrel transport limit
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 50 * 1024 * 1024; // 50 MB
});

// 2. Form data limit (separate from Kestrel)
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 50 * 1024 * 1024; // 50 MB — must match
});
```

Both limits must accommodate the same upload size. Kestrel rejects at the transport layer before the form parser sees data; `FormOptions` rejects inside the parser.

---

## Controller Action

```csharp
[HttpPost("upload")]
[RequestSizeLimit(50 * 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = 50 * 1024 * 1024)]
public async Task<Results<Ok<UploadResponse>, BadRequest<ProblemDetails>>> UploadAsync(
    IFormFile file,
    CancellationToken cancellationToken)
{
    // 1. Extension check (first gate — fast)
    var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (!_allowedExtensions.Contains(extension))
        return TypedResults.BadRequest(Problem("File type not allowed."));

    // 2. Magic byte validation (second gate — reliable)
    if (!await IsValidMagicBytesAsync(file, cancellationToken))
        return TypedResults.BadRequest(Problem("File content does not match declared type."));

    // 3. Generate safe filename — NEVER use the client-provided filename
    var storedName = $"{Guid.NewGuid()}{extension}";

    await _storageService.StoreAsync(storedName, file.OpenReadStream(), cancellationToken);

    return TypedResults.Ok(new UploadResponse(storedName));
}
```

---

## Magic Byte Validation

Extension checks are spoofable. Validate the actual file header:

```csharp
private static readonly Dictionary<string, byte[]> MagicBytes = new()
{
    { ".pdf",  [0x25, 0x50, 0x44, 0x46] },          // %PDF
    { ".png",  [0x89, 0x50, 0x4E, 0x47] },          // .PNG
    { ".jpg",  [0xFF, 0xD8, 0xFF] },                 // JPEG SOI
    { ".zip",  [0x50, 0x4B, 0x03, 0x04] },           // PK..
};

private static async Task<bool> IsValidMagicBytesAsync(IFormFile file, CancellationToken ct)
{
    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (!MagicBytes.TryGetValue(ext, out var magic)) return false;

    var header = new byte[magic.Length];
    await using var stream = file.OpenReadStream();
    var read = await stream.ReadAsync(header.AsMemory(0, magic.Length), ct);

    return read == magic.Length && header.SequenceEqual(magic);
}
```

---

## Filename Safety

**Never persist the client-provided filename.** Client filenames can contain path traversal sequences (`../../etc/passwd`), null bytes, or reserved names.

```csharp
// WRONG — path traversal vulnerability
var path = Path.Combine(uploadDir, file.FileName);

// CORRECT — always generate a new name
var saredName = $"{Guid.NewGuid()}{Path.GetExtension(file.FileName).ToLowerInvariant()}";
```

Store original filename in a database/metadata record only for display purposes.

---

## Anti-Forgery for Browser Uploads

Multi-part form submissions from browsers require anti-forgery validation:

```csharp
// Program.cs
builder.Services.AddAntiforgery();
app.UseAntiforgery();

// Controller — require valid token
[ValidateAntiForgeryToken]
[HttpPost("upload")]
public async Task<IActionResult> UploadAsync(IFormFile file) { ... }
```

For API clients (mobile, non-browser), anti-forgery is not needed — only add it if the upload endpoint is called from a browser form.

---

## Multiple Files

```csharp
[HttpPost("upload-many")]
public async Task<Ok<IEnumerable<UploadResponse>>> UploadManyAsync(
    IFormFileCollection files,
    CancellationToken cancellationToken)
{
    var results = new List<UploadResponse>();
    foreach (var file in files)
    {
        // validate and store each file independently
    }
    return TypedResults.Ok(results.AsEnumerable());
}
```

---

## Streaming Large Files (Skip IFormFile Buffering)

For files > 100 MB, bypass `IFormFile` (which buffers to disk) and stream directly:

```csharp
[DisableRequestSizeLimit]
[HttpPost("upload-stream")]
public async Task<IActionResult> UploadStreamAsync()
{
    if (!Request.HasFormContentType) return BadRequest();

    var reader = new MultipartReader(Request.GetMultipartBoundary(), Request.Body);
    MultipartSection? section;

    while ((section = await reader.ReadNextSectionAsync()) != null)
    {
        var header = ContentDispositionHeaderValue.Parse(section.ContentDisposition);
        if (header.IsFileDisposition())
        {
            var fileName = $"{Guid.NewGuid()}{Path.GetExtension(header.FileName.Value)}";
            await _storageService.StoreAsync(fileName, section.Body, HttpContext.RequestAborted);
        }
    }

    return Ok();
}
```

---

## References

- [File uploads in ASP.NET Core](https://learn.microsoft.com/aspnet/core/mvc/models/file-uploads)
- [Kestrel request body size limits](https://learn.microsoft.com/aspnet/core/fundamentals/servers/kestrel/options#maximum-request-body-size)
- [Large file uploads with streaming](https://learn.microsoft.com/aspnet/core/mvc/models/file-uploads#upload-large-files-with-streaming)
