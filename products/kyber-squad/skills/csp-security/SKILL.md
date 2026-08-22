---
name: csp-security
description: OWASP ASVS L2 security guidance (CSP, headers, HSTS, rate limiting, authz, input/output handling).
license: MIT
metadata:
  author: David R Palfery
  version: 1.0.0
---

# CSP / Security Headers / OWASP ASVS L2 Patterns

**Trigger:**
This skill MUST be loaded whenever working on:
- Security headers, CSP policies, or HSTS configuration.
- Rate limiting, throttling, or abuse prevention.
- Authorization policies, `[Authorize]` attributes, or role-based access control.
- Input validation, sanitization, or output encoding.
- CORS configuration or cross-origin security.
- Middleware pipeline ordering in `Program.cs`.
- API or BFF endpoints, security middleware, or `Program.cs` pipeline ordering.

---

## Security Target: OWASP ASVS Level 2

This project targets **OWASP Application Security Verification Standard (ASVS) Level 2**. All security decisions must be evaluated against this standard.

---

## Security Headers Middleware

### API: `SecurityHeadersMiddleware.cs`
Location: the API project's security-headers middleware (name it after the host's existing type).

**Required Headers (all responses):**
| Header | Value | Purpose |
|---|---|---|
| `Content-Security-Policy` | `default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'` | Strict CSP blocking external resources |
| `Strict-Transport-Security` | `max-age=63072000; includeSubDomains; preload` | HSTS with 2-year max-age |
| `X-Content-Type-Options` | `nosniff` | Prevent MIME sniffing |
| `X-Frame-Options` | `DENY` | Prevent clickjacking |
| `X-XSS-Protection` | `0` | Disable legacy XSS filter (CSP handles this) |
| `Referrer-Policy` | `strict-origin-when-cross-origin` | Control referrer information |
| `Permissions-Policy` | Block most features (camera, microphone, geolocation, etc.) | Minimize attack surface |

### BFF: Inline Security Headers in `Program.cs`
Location: the BFF project's `Program.cs`.

**Environment-Dependent CSP:**
- **Development**: Allows `unsafe-inline` for React HMR hot reload (`script-src 'self' 'unsafe-inline'`)
- **Production**: Strict CSP matching API policy — NO `unsafe-inline`

**Why no `unsafe-inline` in production:**
- Pigment CSS is zero-runtime, CSP-compliant — no inline styles needed
- React builds produce bundled JS files — no inline scripts needed
- `unsafe-inline` defeats CSP protection against XSS

### Host Header Validation
Both API and BFF implement `HostHeaderValidationMiddleware` to prevent host header injection attacks. Validates incoming `Host` header against an allowlist.

---

## Rate Limiting

### Configuration (API `Program.cs`)
Uses `RateLimitPartition` with role-based policies partitioned by user OID. The per-role hourly/daily limits are declared as **Auth Design** (§7 Rate Limiting) in the root `AGENTS.md` registry — do not restate the specific numbers here, they drift independently of this skill.

### Implementation Rules
- Partition key: User's OID from JWT claims
- Policy names should be descriptive and role-based
- Apply `[EnableRateLimiting("policy")]` on controllers or endpoints
- Never hardcode rate limit values — use configuration

---

## Authorization

### Policies
The specific policy names, scope/role requirements, and client-isolation (`azp` claim) rules are declared as **Auth Design** (§5.2, §5.3, §6) in the root `AGENTS.md` registry — do not restate them here. In general: every endpoint requires an authenticated user; admin endpoints additionally require the admin scope, admin role, and client-isolation check.

### Patterns
```csharp
// Default: require authentication
[Authorize]
public class MotorcycleController : ControllerBase

// Admin: require admin policy with client isolation
[Authorize(Policy = "mcr-api-admin")]
public class WebSourcesAdminController : ControllerBase
```

---

## Input Validation

### Current Pattern: DataAnnotations
```csharp
public class MotorcycleQueryRequest
{
    [Required]
    [StringLength(1000)]
    public string Query { get; set; }
}
```

### Rules
- **ALL** user inputs must be validated before processing
- Use `[Required]`, `[StringLength]`, `[Range]` as minimum validation
- For complex business rules, FluentValidation is acceptable
- **NEVER** trust client-side validation alone
- **NEVER** use raw user input in SQL — always parameterized (see `dapper-sql` skill)
- **NEVER** render raw user input in HTML responses — always encode

---

## CORS Configuration

### API CORS Policy
- Explicit allowed origins (no wildcards in production)
- Explicit allowed methods (`GET`, `POST`, `PUT`, `DELETE`)
- Explicit allowed headers
- `AllowCredentials()` only when specific origins are configured

### Rules
- **NEVER** use `AllowAnyOrigin()` with `AllowCredentials()`
- **NEVER** use wildcard `*` origins in production
- Origins come from configuration, not hardcoded

---

## Middleware Pipeline Order (CRITICAL)

Security middleware MUST be ordered correctly in `Program.cs`:

```
1. UseSecurityHeaders()          // First — applies to ALL responses
2. UseHostHeaderValidation()     // Early — reject bad hosts immediately
3. UseHttpsRedirection()         // Force HTTPS
4. UseHsts()                     // HSTS headers
5. UseCors()                     // CORS before auth
6. UseAuthentication()           // Authenticate
7. UseAuthorization()            // Authorize
8. UseRateLimiter()              // Rate limit after auth (needs user identity)
9. MapControllers()              // Endpoints last
```

---

## Secrets Management (Reinforced)

- **NEVER** store secrets in appsettings, code, or any committed file
- Use `Environment.GetEnvironmentVariable()` or Azure Key Vault
- Connection strings are secrets — environment variables only
- Client secrets: environment variables (e.g., `MCR_BFF_CLIENT_SECRET`)
- API keys: environment variables or Key Vault references

---

## Logging Security

- **NEVER** log raw user queries, prompts, or search terms (PII)
- **NEVER** log tokens, secrets, or connection strings
- **ALWAYS** sanitize user input before logging (strip newlines, tabs)
- **ALWAYS** use structured logging with placeholders: `_logger.LogInfo("User {UserId} action", sanitizedId)`
- **NEVER** use string concatenation in log messages

---

## MUST NOT
- Add `unsafe-inline` or `unsafe-eval` to CSP in production
- Disable HTTPS redirection or HSTS
- Use wildcard CORS origins in production
- Skip authorization on any endpoint that accesses data
- Log sensitive data (tokens, secrets, PII, raw queries)
- Trust client-side validation as the only validation layer

## MUST DO
- Include all security headers on every response
- Validate host headers against allowlist
- Apply rate limiting based on user identity and role
- Validate `azp` claim on admin endpoints for client isolation
- Validate all user inputs with DataAnnotations at minimum
- Use parameterized queries for all database operations
- Enforce HTTPS with HSTS preload
- Follow correct middleware pipeline ordering
