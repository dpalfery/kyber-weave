---
name: bff-yarp
description: BFF / YARP reverse proxy patterns for the WebUI BFF.
license: MIT
metadata:
  author: David R Palfery
  version: 1.0.0
---

# BFF / YARP Reverse Proxy Patterns

**Trigger:**
This skill MUST be loaded whenever working on:
- The host BFF / YARP project (reverse proxy in front of the API).
- YARP reverse proxy configuration, routes, or transforms.
- Authentication flows between the React WebUI and backend API.
- Token forwarding, cookie-based auth, or OIDC integration.
- Serving the React SPA through the BFF.

---

## Architecture Overview

The BFF (Backend-for-Frontend) acts as a secure intermediary between the React WebUI and the backend API:

```
React WebUI (SPA) → BFF (YARP + Auth) → Backend API
     Browser             Cookie-based         JWT Bearer
                         OIDC auth            Token forwarding
```

**Key Principle**: The browser never sees or stores access tokens. The BFF handles OIDC authentication, stores tokens server-side in encrypted cookies, and forwards Bearer tokens to the API on each proxied request.

---

## Project Structure

```
<Solution>.WebUI.Bff/
├── Program.cs                              # YARP setup, auth, middleware pipeline
├── appsettings.json                        # YARP ReverseProxy routes & clusters
├── appsettings.Development.json            # Dev-specific CORS, Azure AD settings
├── Controllers/
│   └── AuthController.cs                   # Login/logout/user-info OIDC endpoints
├── Middleware/
│   └── HostHeaderValidationMiddleware.cs   # Host header injection prevention
├── Properties/
│   └── launchSettings.json                 # Local dev launch config
└── wwwroot/                                # React SPA static assets (built output)
    ├── index.html                          # SPA entry point (fallback route)
    └── assets/                             # Compiled JS/CSS bundles
```

---

## YARP Configuration

### Route Definition (`appsettings.json`)
```json
{
  "ReverseProxy": {
    "Routes": {
      "api-route": {
        "ClusterId": "api-cluster",
        "Match": {
          "Path": "/api/{**catch-all}"
        },
        "Transforms": [
          { "PathRemovePrefix": "/api" }
        ]
      }
    },
    "Clusters": {
      "api-cluster": {
        "Destinations": {
          "api": {
            "Address": "https://localhost:7215"
          }
        }
      }
    }
  }
}
```

### Key Behaviors
- `/api/*` requests are proxied to the backend API cluster
- Path transform strips `/api` prefix before forwarding
- Non-API requests fall through to serve the React SPA from `wwwroot/`

---

## Authentication Flow

### OIDC Setup (Program.cs)
- **Scheme**: Cookie authentication + OpenID Connect (Azure AD / Entra External ID)
- **Scopes**: See the scope convention declared as **Auth Design** in the root `AGENTS.md` registry — do not restate the exact scope URIs here, they evolve independently of this skill.
- **Client Secret**: Retrieved via the standard `IConfiguration` / Azure App Configuration + Key Vault process — never via environment variables. See **Configuration Policy**.
- **Token Storage**: Server-side in encrypted cookies (never exposed to browser)

### Auth Endpoints (`AuthController.cs`)
| Endpoint | Method | Purpose |
|---|---|---|
| `/auth/login` | GET | Initiates OIDC challenge flow → redirect to identity provider |
| `/auth/logout` | GET/POST | Signs out of cookie + OIDC session |
| `/auth/user` | GET | Returns current user claims (name, email, roles) |

### Token Forwarding (YARP Transform)
YARP transforms automatically attach the user's access token as a Bearer header on proxied requests:
```csharp
// In YARP transform configuration
GetTokenAsync("access_token") → Authorization: Bearer <token>
```

---

## Security (BFF-Specific)

### CSP Headers
- **Development**: Allows `unsafe-inline` for React HMR
- **Production**: Strict CSP — no `unsafe-inline` (Pigment CSS is CSP-compliant)

### Host Header Validation
`HostHeaderValidationMiddleware` validates incoming `Host` headers against an allowlist to prevent injection attacks.

### CORS
- Configured with specific frontend origins from `appsettings`
- `AllowCredentials()` enabled for cookie-based auth
- No wildcard origins

### SPA Fallback
- `app.UseStaticFiles()` serves React build output from `wwwroot/`
- Fallback route serves `index.html` for client-side routing
- Static assets include cache headers for production optimization

---

## Dependencies (`MotorcycleRag.WebUI.BFF.csproj`)
- `Yarp.ReverseProxy` 2.3.0
- ASP.NET Core Authentication (Cookies + OpenIdConnect)
- .NET 10.0

---

## Middleware Pipeline Order

```
1. Security Headers (inline middleware)
2. Host Header Validation
3. HTTPS Redirection + HSTS
4. CORS
5. Static Files (wwwroot/)
6. Authentication
7. Authorization
8. MapReverseProxy() — YARP proxied routes
9. SPA Fallback (index.html)
```

---

## Adding New Proxied Routes

To add a new backend route through the BFF:

1. Add route in `appsettings.json` under `ReverseProxy.Routes`
2. Define the match pattern and cluster
3. Add path transforms if prefix stripping is needed
4. Test that authentication tokens are forwarded correctly
5. Verify CORS allows the new endpoint's methods

---

## MUST NOT
- Expose access tokens to the browser (they stay server-side in cookies)
- Store client secrets in appsettings, code, or environment variables — use the Azure App Configuration + Key Vault process (see **Configuration Policy**)
- Add `unsafe-inline` to production CSP
- Use wildcard CORS origins
- Bypass YARP for direct API calls from the BFF (defeats the proxy pattern)
- Serve API responses directly from BFF controllers (except auth endpoints)

## MUST DO
- Forward Bearer tokens via YARP transforms on all proxied API requests
- Use cookie-based authentication for browser sessions
- Validate host headers on all incoming requests
- Serve React SPA from `wwwroot/` with proper fallback routing
- Apply security headers to all responses (including static files)
- Keep YARP route configuration in `appsettings.json` (not hardcoded in `Program.cs`)
- Test auth flow end-to-end: Login → Cookie → Proxy → Bearer → API
