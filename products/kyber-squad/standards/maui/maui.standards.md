---
id: standards/maui
title: "MAUI coding standard"
doc-type: coding-standard
status: draft
technology: maui
owner: unassigned
last-reviewed: 2026-11-22
---

# MAUI coding standard

How .NET MAUI is written in this repository. Agents and skills resolve this document as
`<maui-coding-standard>`. Language-level C# decisions live in `<csharp-coding-standard>`;
this file is the MAUI architecture and UI overlay.


When this standard is in `status: current`, it is the rule for this technology in this
repository. Portable agents ship no built-in default to fall back on. While it is in
`status: draft` it is a proposal, but a worker never stops for it: it uses the draft as the
best available guidance and names the draft standard it relied on in one line of its report.

> Template. Set `owner` to a row in `catalog.md`, review the decisions below, and promote
> `status` to `current`. Every choice here is a guess about a repository this template has
> never seen — reversing one is the point of the standard being project-specific.

## Naming and layout

### Stack

- **Target:** .NET MAUI on .NET 10.
- **Architecture:** MVVM with CommunityToolkit.Maui (`ObservableObject`,
  `[ObservableProperty]`, `[RelayCommand]`). This is the Golden Path from Microsoft's
  Enterprise Application Patterns (eShop).
- **Navigation:** Shell exclusively. No `NavigationPage` stacks beside it.
- **Composition:** constructor DI, registered in `MauiProgram.cs`.

## Coding standards

### MVVM

Views are XAML with almost no code-behind.

ViewModels inherit `ObservableObject`, or `ObservableValidator` when the form needs
DataAnnotations. Models are plain records or classes acting as DTOs. Services hold
business logic and data access, always behind an interface.

- **State:** `[ObservableProperty]` for every bindable field.
- **Commands:** `[RelayCommand]` for user actions. Prefer `AsyncRelayCommand` for I/O so
  `IsRunning` is handled by the toolkit.
- **Validation:** `ObservableValidator` with DataAnnotations (`[Required]`,
  `[EmailAddress]`). Do not invent a second validation wrapper.

Data binding rules:

- Set `x:DataType` to the ViewModel type so bindings compile, on the template and not on
  children: `x:DataType="x:Object"` on a child element re-enables reflection and defeats
  compile-time checking.
- Make XAML binding warnings XC0022 and XC0025 build errors (`MSBuildWarningsAsErrors`).
- Set `BindingContext` by constructor injection of the ViewModel into the page, not by
  instantiating it in XAML.
- Omit `Mode` when the control's default fits; set `TwoWay` for form fields only.
- Reach the page ViewModel from inside a `DataTemplate` with
  `Source={RelativeSource AncestorType={x:Type ContentPage}}, Path=BindingContext.<Command>`.
- Property-change notifications are marshalled to the UI thread; `ObservableCollection<T>`
  mutations are not: dispatch them with `MainThread.BeginInvokeOnMainThread`.
- Code-side bindings use the typed, reflection-free `SetBinding(prop, static (VM vm) => vm.X)`.

A new page is a triad: `IPageService` (if the page needs a service), `PageViewModel`, and
`PageView`. Register the ViewModel and View in `MauiProgram.cs` in the same change that adds
the page.

### Dependency injection

All dependencies are registered in `MauiProgram.cs`.

```csharp
builder.Services.AddSingleton<ISettingsService, SettingsService>();
builder.Services.AddSingleton<INavigationService, MauiNavigationService>();

builder.Services.AddHttpClient<ICatalogService, CatalogService>(client =>
        client.BaseAddress = new Uri(GlobalSettings.BaseEndpoint))
    .AddStandardResilienceHandler();

builder.Services.AddTransient<LoginViewModel>();
builder.Services.AddTransient<LoginView>();
```

Lifetimes: Singleton for shared, expensive services and caches; Transient for pages and
ViewModels so each navigation gets fresh state. Avoid Scoped: it does not map to navigation
lifetime. Shell resolves its whole graph (page, ViewModel, services) from the container.
Never resolve with `ServiceProvider.GetService<T>()` inside a ViewModel; use constructor
injection. Container-dependent XAML resources (converters) run before the container is ready:
defer that work to `CreateWindow()` or `OnAppearing()`. Platform-conditional registration
(`#if ANDROID` ... `#elif IOS || MACCATALYST`) always ends in an `#else` default or a throw.

Typed `HttpClient` via `IHttpClientFactory` is the outbound HTTP pattern.
`AddStandardResilienceHandler()` (Microsoft.Extensions.Http.Resilience) owns retries;
do not wrap every call site in its own Polly policy.

### Resilience and connectivity

Remote calls check `Connectivity.Current.NetworkAccess` before they leave the device.
Transient HTTP failures (408, 503) are the resilience handler's job.

Cache-aside: consult the local cache or SQLite store before hitting the API.

### Navigation

Shell only. An `INavigationService` wraps `Shell.Current.GoToAsync` so ViewModels stay
testable. Register routes in `AppShell.xaml.cs`. Incoming navigation data arrives through
`IQueryAttributable` or `[QueryProperty]`; prefer `IQueryAttributable` on the ViewModel, and
pass complex objects with `ShellNavigationQueryParameters`.

- Register detail pages (those outside the Shell hierarchy) with a route string. A duplicate
  route name gives unpredictable navigation.
- Use `ShellContent` with `ContentTemplate`/`DataTemplate`, not `Content`: `Content`
  instantiates every page at startup.
- Without the `await` on `GoToAsync` the target is not ready when data is passed. Absolute
  routes start `//`; back is `..`.
- A navigation guard that does async work (unsaved changes, auth) lives in `OnNavigating`,
  checks `args.CanCancel`, and calls `deferral.Complete()` on every path; without the early
  deferral navigation completes before the check.
- `OnBackButtonPressed` fires on Android only. Cover unsaved-change protection with the Shell
  `OnNavigating` guard so iOS and Mac Catalyst are protected too.

### Settings

`Preferences.Get` does not appear in a ViewModel. An `ISettingsService` wraps
`Microsoft.Maui.Storage.Preferences`; the ViewModel takes the interface.

### CollectionView

- Navigate on selection with either the `SelectedItem` binding or a `SelectionChanged`
  handler, never both: both together run the action twice per tap.
- Pull-to-refresh: wrap in `RefreshView`; the command sets `IsRefreshing` false in `finally`.
- Incremental loading uses `RemainingItemsThreshold` with a command, not the `Scrolled` event.
  The command fires repeatedly while the user crosses the threshold band: guard with an
  `_isLoadingMore` flag that is set before the fetch and cleared in `finally` (never left set),
  and stop when there are no more pages.
- Grouped lists use `IsGrouped="True"` with a `GroupHeaderTemplate` whose `x:DataType` is the
  group key type.
- Keep cell layouts flat; add items to an `ObservableCollection` in one batch or by resetting
  it, not one `Add` per UI update; set `HasUnevenRows` only when cells genuinely vary.
- State-dependent empty views (loading versus empty) use `EmptyViewTemplate` with a
  `DataTemplateSelector`.

### Safe area (.NET 10)

- `ContentPage` defaults to `SafeAreaEdges.None` (edge-to-edge) on .NET 10. Content cut off
  under the notch or home indicator after an upgrade is fixed with `Shell.SafeAreaEdges`
  (`All`, `Top`, `Bottom`, `Left`, `Right`) on the page or a global `ContentPage` style.
- Interactive controls stay inside the safe area. A decorative background may bleed under the
  bars (`SafeAreaEdges="Bottom"` with a full-bleed image); use `Shell.Current.GetSafeAreaInsets()`
  for manual margins.
- Wrap forms in a `ScrollView` so the keyboard does not cover inputs; use the CommunityToolkit
  `KeyboardOverlapBehavior` for finer control.
- On Android, call `WindowCompat.SetDecorFitsSystemWindows(Window!, false)` in
  `MainActivity.OnCreate` so insets are reported correctly on API levels below the enforced
  edge-to-edge level (35).

### Theming

- Define colours once as named resources in `App.xaml`, named by use (`PageBackground`,
  `PrimaryText`, `AccentColor`), not by palette. No hard-coded hex in pages.
- Use `AppThemeBinding Light=..., Dark=...` with resource references for per-theme values.
- A colour that can change while the app runs (manual theme toggle) is a `DynamicResource`;
  `StaticResource` resolves once at load and stays stale.
- Theme toggle sets `Application.Current.UserAppTheme` (`Unspecified` follows the OS); persist
  the choice through the settings service and restore it at startup. React to OS changes
  through `RequestedThemeChanged`.
- Without `ConfigChanges.UiMode` (listed with `ScreenSize`, `Orientation`) every dark/light
  toggle restarts the Activity and loses state.
- `AppThemeBinding` is unsupported on Android below API 29; handle those through
  `RequestedThemeChanged`.
- Style controls with implicit `Style TargetType` entries that point at the semantic resources.

### App lifecycle

- Subscribe to `Window` events in `CreateWindow`: `Created` (one-time setup), `Resumed` (refresh
  stale data, restart timers), `Deactivated` (pause non-critical work), `Stopped` (pause timers
  and subscriptions, save light state), `Destroying` (release unmanaged resources).
- A blocked lifecycle handler is an ANR on Android; start async work with `_ = LoadAsync()`
  or `Task.Run`.
- `OnAppearing` runs on every return to the page, Back included: guard expensive loads with a
  `_loaded` flag or load on fresh navigation only, and cancel pending work in `OnDisappearing`.
- Do not rely on `Stopped` for critical data (a force-quit skips it): persist incrementally as
  the ViewModel changes. Complex state goes as JSON under `FileSystem.AppDataDirectory`.
- Keep platform lifecycle overrides (`Platforms/Android/MainActivity.cs`) thin and delegate
  to a shared service.

## Testing

### Testability

Tests mock `INavigationService` and data services; this standard only requires that the
code be mockable. Test ViewModels and services, not Views. When a test needs the container,
build a minimal `ServiceCollection` with the ViewModel and substitutes; never start
`MauiProgram`.

## Commands

_Nothing specific yet._

## Security and data handling

### Authentication

OIDC/OAuth2 with PKCE. Entra ID is the primary identity provider, via
Microsoft.Identity.Client (MSAL.NET) so the native broker is available.
`IdentityModel.OidcClient` is the fallback only when the provider is a generic OIDC host.

Tokens live in `ISecureStorage`, reached through `ISettingsService` — never in
Preferences, never in a literal.

## Done checklist

### Completion

```bash
dotnet build -c Release                                 # the build, analyzers and XAML compilation included
dotnet test --filter "FullyQualifiedName~<TestClass>"   # the ViewModel and service tests covering your change
dotnet format --verify-no-changes                       # the formatter check
dotnet build -t:Run -f net10.0-android                  # run it on a device or emulator
dotnet build -t:Run -f net10.0-ios
```

`dotnet build -c Release` is the build gate: a XAML error, a broken binding or a missing resource
fails here rather than on a device. The filtered `dotnet test` line is what a dev agent runs; the
unfiltered suite belongs to the review council and CI. `dotnet format --verify-no-changes` is the
format check. Views have no unit tests under this standard, so a change to a View is proved by the
ViewModel tests plus a run on a target — say which target you ran.
