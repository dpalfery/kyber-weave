---
id: plans/2026-10-17-code-scanning-273-open-browser
title: "Fix Code Scanning Alert 273: Windows Browser Opener Indirect Command Injection and Parameter Truncation"
doc-type: plan
status: complete
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-18
development-mode: test-first
---

# Fix Code Scanning Alert 273: Windows Browser Opener Indirect Command Injection and Parameter Truncation

**Status: Complete 2026-10-18.** Approved by user ("approve") 2026-10-18. Authoritative decisions D1–D4 recorded below under Conductor mandate and user approval. Primary decisions D1, D2, and D4 harvested into [docs/dash/architecture.md](../../dash/architecture.md) per KW-DOC-LIFECYCLE-003. Archived per KW-DOC-LIFECYCLE-003.
Development mode: `test-first`.

---

## 1. Problem and Goal

### 1.1 Context and Evidence

GitHub Code Scanning alert 273 flags `openBrowser(url: string)` in `dash/src/cli/web.ts` (lines 193–214) with rule `js/indirect-command-line-injection`:

```typescript
export function openBrowser(url: string): void {
  const [program, args] =
    process.platform === 'darwin'
      ? ['open', [url]]
      : process.platform === 'win32'
        ? ['cmd', ['/c', 'start', '', url]]
        : ['xdg-open', [url]]
  try {
    execFile(program!, args as string[], () => {
      /* a browser that fails to launch is not an error the server should raise */
    })
  } catch {
    /* user can open it manually */
  }
}
```

Although `execFile` does not spawn an intermediate shell by itself, on Windows the target program explicitly *is* `cmd` with arguments `['/c', 'start', '', url]`.

This introduces two concrete problems:
1. **Security Vulnerability (CodeQL Alert 273):** Passing user-controllable input (from `--view` via `joinViewUrl(url, opts.view)`) into `cmd.exe /c` represents an indirect command line injection sink.
2. **Functional Defect (Windows Query Parameter Truncation):** In `cmd.exe`, the ampersand character `&` acts as an unconditional command separator. When opening a route with multiple query parameters (for example, `/compare?a=:runId&b=:runId` from `dash/src/server/view-paths.json`), `cmd.exe` splits the URL at `&`, launches the browser with only the first parameter `http://127.0.0.1:4747/compare?a=run1`, and then attempts to execute `b=run2` as a shell command. The browser never receives `b=run2`.

Additionally, neither `openBrowser` nor `joinViewUrl` independently validates that the URL being opened is a legitimate loopback dashboard URL. While `runWebDashboard` checks `matchesViewPath(opts.view)`, `openBrowser` is also invoked directly in `dash/src/cli/program.ts` when attaching to an existing engine:
```typescript
if (opts.open && err.record.url !== undefined) openBrowser(joinViewUrl(err.record.url, opts.view))
```
Defense in depth requires `openBrowser` and `joinViewUrl` to validate the URL before invoking `execFile`.

### 1.2 Goal

1. Eliminate the `cmd.exe /c` shell invocation on Windows by switching the Windows browser launcher to `['rundll32.exe', ['url.dll,FileProtocolHandler', safeUrl]]`. This resolves Code Scanning alert 273 and guarantees that `&` query parameters (such as `/compare?a=:runId&b=:runId`) are passed intact to the browser without truncation.
2. Enforce strict loopback origin and view route validation before launching the browser in `dash/src/cli/web.ts`, mirroring the Rust tray's `open_view_url` and `loopback_origin` invariants (`dash/tray/src-tauri/src/ipc.rs` and `dash/tray/src-tauri/src/api.rs`).
3. Maintain 100% test-first development mode with comprehensive Vitest coverage across Windows, macOS, and Linux opener branches, negative loopback injection checks, and traversal rejection tests.

---

## 2. Decisions

The table below records the permanent architectural decisions governing this change.

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| **D1** | How should Windows launch the default browser without invoking `cmd.exe /c` or truncating query parameters containing `&`? | Replace `['cmd', ['/c', 'start', '', url]]` with `['rundll32.exe', ['url.dll,FileProtocolHandler', safeUrl]]` in `openBrowser` (`dash/src/cli/web.ts`). | `rundll32.exe url.dll,FileProtocolHandler <url>` invokes the Windows Shell Protocol Handler directly via `execFile`, bypassing `cmd.exe` entirely. This eliminates the shell command injection sink flagged by CodeQL alert 273 and preserves `&` in query parameters (preventing parameter truncation on `/compare?a=:runId&b=:runId`). Alternatives rejected: `powershell.exe Start-Process` (spawns PowerShell shell, execution policy friction, slow startup); `cmd.exe` caret escaping (fragile, still flagged by CodeQL); native C++ node addon (unnecessary dependency and compilation overhead). | Approved by user ("approve") 2026-10-18 |
| **D2** | How should URLs be validated before being handed to the system browser opener in `dash/src/cli/web.ts`? | Validate and canonicalize the URL in `joinViewUrl` and `openBrowser`, mirroring the Rust tray's `open_view_url` (`dash/tray/src-tauri/src/ipc.rs`) and `loopback_origin` (`dash/tray/src-tauri/src/api.rs`):<br>1. Origin must be strictly `http://127.0.0.1` or `http://127.0.0.1:<port>` with integer port `1..65535` (rejecting any `@`, `\`, `%`, or credentials).<br>2. Path/query must not contain `://` (scheme injection), leading `//` (protocol-relative injection), or `..` path traversal segments.<br>3. Path/query must pass `matchesViewPath(view)` against `src/server/view-paths.json`.<br>4. `openBrowser(url)` refuses any invalid URL and returns without calling `execFile`. | Defense in depth. While `runWebDashboard` checks `matchesViewPath`, `openBrowser` is also called directly from `dash/src/cli/program.ts` on engine attachment. CodeQL and secure coding require validating at the sink (`openBrowser`) before invoking `execFile`. Mirrored directly from the Rust tray implementation so CLI and tray share identical URL admission rules. | Approved by user ("approve") 2026-10-18 |
| **D3** | Where should automated unit tests for `openBrowser`, `joinViewUrl`, and loopback URL validation live? | Author a dedicated test file `dash/src/cli/web-open-browser.test.ts` in Vitest. | CodeGraph showed no existing tests covering `openBrowser`. `web.test.ts` and `web-dashboard.test.ts` test HTTP server lifecycle and API routes. A dedicated unit test file provides focused, fast, hermetic coverage of `openBrowser`, `joinViewUrl`, and URL validation with mocked `process.platform` and `child_process.execFile`. | Approved by user ("approve") 2026-10-18 |
| **D4** | How should the URL validation logic be structured in `dash/src/cli/web.ts`? | Encapsulate the loopback origin and view path validation in an exported helper `validateDashboardUrl(url: string): string | null`. | A pure function returning the canonical safe URL string on success and `null` on rejection enables clean, comprehensive unit testing against a matrix of valid and malicious URLs, while `openBrowser` and `joinViewUrl` consume it directly. | Approved by user ("approve") 2026-10-18 |

---

## 3. Investigation Findings

1. **Existing `openBrowser` implementation (`dash/src/cli/web.ts:193-214`):**
   ```typescript
   export function openBrowser(url: string): void {
     const [program, args] =
       process.platform === 'darwin'
         ? ['open', [url]]
         : process.platform === 'win32'
           ? ['cmd', ['/c', 'start', '', url]]
           : ['xdg-open', [url]]
     try {
       execFile(program!, args as string[], () => {})
     } catch {}
   }
   ```
   - On Windows, `program` is `'cmd'` and `args` is `['/c', 'start', '', url]`.
   - In `cmd.exe`, `&` is a command separator token. For a URL such as `http://127.0.0.1:4747/compare?a=run-1&b=run-2`, `cmd.exe` treats `b=run-2` as a second command to execute. The browser receives only `http://127.0.0.1:4747/compare?a=run-1`.
   - CodeQL flags this under `js/indirect-command-line-injection`.

2. **Existing `joinViewUrl` implementation (`dash/src/cli/web.ts:314-317`):**
   ```typescript
   export function joinViewUrl(origin: string, view: string | undefined): string {
     if (view === undefined || view.trim() === '' || view.trim() === '/') return origin
     return `${origin}/${view.trim().replace(/^\/+/, '')}`
   }
   ```
   - `joinViewUrl` currently performs no origin verification, allows traversal segments (`..`), and does not enforce route matching.

3. **Rust Tray Parity Analysis:**
   - In `dash/tray/src-tauri/src/api.rs`:
     `loopback_origin(base: &str)` verifies that the base URL starts with `http://`, the authority contains no `@`, `\`, or `%`, host is strictly `127.0.0.1`, port is an integer in `1..65535`, and trailing tail has no path/query.
   - In `dash/tray/src-tauri/src/ipc.rs`:
     `open_view_url(server_url: &str, view: &str)` verifies `loopback_origin`, rejects any `://`, leading `//`, or `..` segments, and verifies `matches_view_path(view)`.
   - `dash/src/server/view-paths.ts` already exports `matchesViewPath(view: string): boolean` backed by `dash/src/server/view-paths.json`.

---

## 4. Test-First Contract (`development-mode: test-first`)

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| **T1** | `dash/src/cli/web-open-browser.test.ts` | `npm --prefix dash run test -- src/cli/web-open-browser.test.ts` | 1. On `win32`, `openBrowser` calls `execFile` with `rundll32.exe` and `['url.dll,FileProtocolHandler', safeUrl]`, NOT `cmd` or `cmd.exe`.<br>2. On `win32`, a view URL with `&` query parameters (e.g. `http://127.0.0.1:4747/compare?a=123&b=456`) passes the intact URL containing `&` to `rundll32.exe`.<br>3. `openBrowser` succeeds for valid dashboard routes (`http://127.0.0.1:4747`, `http://127.0.0.1:4747/`, `http://127.0.0.1:4747/run/abc`, `http://127.0.0.1:4747/compare?a=1&b=2`, `http://127.0.0.1:4747/finding/xyz`, `http://127.0.0.1:4747/session/s1/turn/0`).<br>4. `openBrowser` refuses non-loopback origins (`https://google.com`, `http://localhost:4747`, `http://192.168.1.1:4747`, `file:///etc/passwd`, `http://127.0.0.1:4747@evil.example`, `http://127.0.0.1:0`) and does NOT call `execFile`.<br>5. `openBrowser` refuses off-route paths or path traversals (`http://127.0.0.1:4747/../secret`, `http://127.0.0.1:4747//evil.example`, `http://127.0.0.1:4747/admin`) and does NOT call `execFile`.<br>6. `joinViewUrl` correctly validates and joins valid view paths, and rejects traversal or off-route view inputs. | Before any production edits, running `npm --prefix dash run test -- src/cli/web-open-browser.test.ts` must fail with clear assertion failures on `cmd` vs `rundll32.exe`, non-loopback URL refusal, and query parameter handling. | `npm --prefix dash run test -- src/cli/web-open-browser.test.ts` exits 0 with all assertions passing without weakening any test assertions. |
| **T2** | `dash/src/cli/web.ts` and `dash/src/cli/web-open-browser.test.ts` | `npm --prefix dash run test -- src/cli/web-open-browser.test.ts` | Implementation of `validateDashboardUrl`, updated `openBrowser`, and updated `joinViewUrl` satisfies all T1 contract requirements. | T1's captured RED evidence required before T2 implementation. | T1 contract tests pass GREEN cleanly. |
| **T3** | Full Dash test suite and TypeScript quality gates | `npm --prefix dash run test && npm --prefix dash run typecheck && npm --prefix dash run lint && npm --prefix dash run check:reachable` | Full regression pass across all 265+ test files, zero TypeScript compilation errors, zero ESLint warnings, and clean reachable check. | n/a — regression and quality verification task over T2. | All four commands exit 0. |
| **T4** | Documentation checks (`docs validate .` and `docs drift .`) | `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .` and `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .` | `docs/dash/architecture.md` accurately documents the browser opener Windows contract and loopback validation invariants; plan closeout mapping is complete; no documentation drift. | n/a — explicitly no-test documentation task. | Both documentation validation and drift checks report 0 findings. |

---

## 5. Dispatchable Tasks

### T1 — RED: Author failing unit tests for browser opening and loopback URL validation

- **Objective:** Author comprehensive unit tests in `dash/src/cli/web-open-browser.test.ts` reproducing the CodeQL alert 273 sink condition (`cmd /c start`), query parameter truncation, loopback origin enforcement, and route matching.
- **Exact files/symbols:** `dash/src/cli/web-open-browser.test.ts`.
- **Work:**
  - Mock `process.platform` and `child_process.execFile` in Vitest.
  - Assert that on `win32`, `openBrowser` invokes `rundll32.exe` with arguments `['url.dll,FileProtocolHandler', safeUrl]`, and does not invoke `cmd` or `cmd.exe`.
  - Assert that on `win32`, multi-parameter query strings (e.g. `/compare?a=123&b=456`) pass the full URL containing `&` intact to `rundll32.exe`.
  - Assert that on `darwin`, `openBrowser` invokes `open`, and on `linux`, `xdg-open`.
  - Assert that `openBrowser` accepts valid dashboard URLs (`http://127.0.0.1:<port>` with valid view routes from `src/server/view-paths.json`).
  - Assert that `openBrowser` refuses non-loopback schemes, remote hostnames, credentials (`@`), invalid ports, traversal segments (`..`), scheme injection (`://`), protocol-relative slashes (`//`), and unrecognized routes without calling `execFile`.
  - Assert that `joinViewUrl` validates origin and view, throwing `UnknownViewError` on off-route or traversal views.
  - Run the test suite to capture RED evidence.
- **Acceptance criteria:** T1 Test-contract RED evidence captured showing failures on unpatched `dash/src/cli/web.ts`.
- **Dependencies:** None.
- **Required skill:** `test-dev`.

### T2 — GREEN: Implement `rundll32.exe` opener and strict loopback validation in `dash/src/cli/web.ts`

- **Objective:** Update `openBrowser`, `joinViewUrl`, and introduce `validateDashboardUrl` in `dash/src/cli/web.ts` to satisfy T1's test contract.
- **Exact files/symbols:**
  - `dash/src/cli/web.ts:validateDashboardUrl`
  - `dash/src/cli/web.ts:openBrowser`
  - `dash/src/cli/web.ts:joinViewUrl`
- **Work:**
  - Implement `validateDashboardUrl(rawUrl: string): string | null`:
    - Parse `rawUrl` using `new URL(rawUrl)`.
    - Enforce `parsed.protocol === 'http:'` and `parsed.hostname === '127.0.0.1'`.
    - Enforce port is an integer in range `1..65535`.
    - Enforce `parsed.username === ''` and `parsed.password === ''`.
    - Enforce raw authority string does not contain `@`, `\`, or `%`.
    - Extract relative path and search query (`${parsed.pathname}${parsed.search}`).
    - Enforce relative view does not contain `://` or leading `//`.
    - Enforce no segment in `pathname.split('/')` equals `'..'`.
    - Enforce `matchesViewPath(view)` is true.
    - Return canonicalized URL string `http://127.0.0.1:${port}${pathname}${search}` or `null` if invalid.
  - Update `openBrowser(url: string)`:
    - Validate URL via `validateDashboardUrl(url)`. If `null`, safely log/return without calling `execFile`.
    - Select platform opener:
      - `darwin`: `['open', [safeUrl]]`
      - `win32`: `['rundll32.exe', ['url.dll,FileProtocolHandler', safeUrl]]`
      - other: `['xdg-open', [safeUrl]]`
    - Invoke `execFile(program, args, () => {})`.
  - Update `joinViewUrl(origin: string, view: string | undefined)`:
    - If `view` is empty/undefined/`/`, validate and return canonical loopback origin.
    - If `view` is specified, construct target URL and validate with `validateDashboardUrl`. If invalid, throw `UnknownViewError(view, formatValidViewForms())`.
- **Acceptance criteria:** T1 tests pass GREEN.
- **Dependencies:** T1.
- **Required skill:** `test-dev`.

### T3 — Full Dash regression suite and TypeScript quality gates

- **Objective:** Validate that all unit tests, type checks, linting, and reachability checks pass cleanly across the `dash` workspace.
- **Exact files/symbols:** `dash/`.
- **Work:**
  - Run `npm --prefix dash run test`.
  - Run `npm --prefix dash run typecheck`.
  - Run `npm --prefix dash run lint`.
  - Run `npm --prefix dash run check:reachable`.
- **Acceptance criteria:** All 4 commands exit 0 with no errors.
- **Dependencies:** T2.
- **Required skill:** `test-dev`.

### T4 — `docs-dev` closeout and canonical documentation update

- **Objective:** Update canonical documentation in `docs/dash/architecture.md`, record closeout mappings and ADR waivers, update plan status, and verify docs validation and drift checks pass.
- **Exact files/symbols:**
  - `docs/dash/architecture.md`
  - `docs/archive/plans/2026-10-17-code-scanning-273-open-browser.md`
  - `docs/plans/README.md`
- **Work:**
  - Update `docs/dash/architecture.md` (Surface Layer) to document:
    - The browser opener contract on Windows (`rundll32.exe url.dll,FileProtocolHandler`) preventing shell command injection and preserving query parameters.
    - The loopback origin and view route validation contract for browser launching.
  - Record the closeout mapping in this plan:
    - D1: harvested to `docs/dash/architecture.md`; no ADR — ADR waiver: bug fix and security hardening under existing KyberDash CLI engine architecture.
    - D2: harvested to `docs/dash/architecture.md`; no ADR — ADR waiver: URL validation refinement aligning CLI with existing Rust tray display layer invariants under ADR 0033.
  - Run `docs validate .` and `docs drift .`.
- **Acceptance criteria:** `docs validate .` and `docs drift .` pass with 0 findings.
- **Dependencies:** T3.
- **Required skill:** `docs-dev`.

---

## 6. Dependency Graph

```text
T1 (RED: Unit tests in web-open-browser.test.ts)
  │
  ▼
T2 (GREEN: Implementation in web.ts)
  │
  ▼
T3 (Verification: Full Dash test suite, typecheck, lint, reachable)
  │
  ▼
T4 (Closeout: docs-dev canonical documentation & drift checks)
```

---

## 7. Risks and Out-of-Scope Boundaries

- **Risks:**
  - *Windows execution environment:* `rundll32.exe` is a standard system binary in `%SystemRoot%\System32` on all supported Windows operating systems. Using `execFile('rundll32.exe', ...)` resolves via `PATH` without shell expansion.
  - *Vitest test mocking:* Mocking `process.platform` and `child_process.execFile` in `web-open-browser.test.ts` must use clean `afterEach` teardown (`vi.restoreAllMocks()`) to prevent state leakage into other test files.
- **Out of Scope:**
  - Modifying the Rust tray (`dash/tray/src-tauri/`) — the tray already enforces `open_view_url` and `loopback_origin`.
  - Adding new dashboard view routes or altering existing client-side SPA routing.
  - Modifying GitHub Actions workflow definitions or CodeQL configuration files.

---

## 8. Closeout Mapping and Waivers

- **D1 (Windows Browser Opener):** Harvested to `docs/dash/architecture.md` (Surface Layer: Browser Opener Contract). No ADR — ADR waiver: security bug fix and Windows opener hardening under existing KyberDash CLI engine architecture.
- **D2 (Loopback Origin & View URL Validation):** Harvested to `docs/dash/architecture.md` (Surface Layer: Browser Opener Contract). No ADR — ADR waiver: validation refinement aligning CLI browser launch with existing Rust tray display layer invariants under ADR 0033.
- **Documentation Waiver:** None. Canonical documentation in `docs/dash/architecture.md` is updated in T4.
