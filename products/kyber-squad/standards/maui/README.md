---
id: standards/maui
title: "MAUI coding standard"
doc-type: coding-standard
status: draft
technology: maui
owner: unassigned
last-reviewed: 2026-08-16
---

# MAUI coding standard

How .NET MAUI is written in this repository. Agents and skills resolve this document as
`<maui-coding-standard>`. Language-level C# decisions live in `<csharp-coding-standard>`;
this file is the MAUI architecture and UI overlay.

## Authority & status

When this standard is in `status: current`, it is the rule for this technology in this
repository. Portable agents ship no built-in default to fall back on. While it is in
`status: draft` it is a proposal: an agent that resolves it says so and asks a human whether
to proceed on it, exactly as it does when no standard is declared.

> Template. Set `owner` to a row in `catalog.md`, review the decisions below, and promote
> `status` to `current`. Every choice here is a guess about a repository this template has
> never seen — reversing one is the point of the standard being project-specific.

## Stack

- **Target:** .NET MAUI on .NET 10.
- **Architecture:** MVVM with CommunityToolkit.Maui (`ObservableObject`,
  `[ObservableProperty]`, `[RelayCommand]`). This is the Golden Path from Microsoft's
  Enterprise Application Patterns (eShop).
- **Navigation:** Shell exclusively. No `NavigationPage` stacks beside it.
- **Composition:** constructor DI, registered in `MauiProgram.cs`.

## MVVM

Views are XAML with almost no code-behind. Every View sets `x:DataType` to its ViewModel
so bindings compile.

ViewModels inherit `ObservableObject`, or `ObservableValidator` when the form needs
DataAnnotations. Models are plain records or classes acting as DTOs. Services hold
business logic and data access, always behind an interface.

- **State:** `[ObservableProperty]` for every bindable field.
- **Commands:** `[RelayCommand]` for user actions. Prefer `AsyncRelayCommand` for I/O so
  `IsRunning` is handled by the toolkit.
- **Validation:** `ObservableValidator` with DataAnnotations (`[Required]`,
  `[EmailAddress]`). Do not invent a second validation wrapper.

A new page is a triad: `IPageService` (if the page needs a service), `PageViewModel`, and
`PageView`. Register the ViewModel and View as transient in `MauiProgram.cs` in the same
change that adds the page.

## Dependency injection

All dependencies are registered in `MauiProgram.cs`. ViewModels never `new` a service.

```csharp
builder.Services.AddSingleton<ISettingsService, SettingsService>();
builder.Services.AddSingleton<INavigationService, MauiNavigationService>();

builder.Services.AddHttpClient<ICatalogService, CatalogService>(client =>
        client.BaseAddress = new Uri(GlobalSettings.BaseEndpoint))
    .AddStandardResilienceHandler();

builder.Services.AddTransient<LoginViewModel>();
builder.Services.AddTransient<LoginView>();
```

Typed `HttpClient` via `IHttpClientFactory` is the outbound HTTP pattern.
`AddStandardResilienceHandler()` (Microsoft.Extensions.Http.Resilience) owns retries;
do not wrap every call site in its own Polly policy.

## Resilience and connectivity

Remote calls check `Connectivity.Current.NetworkAccess` before they leave the device.
Transient HTTP failures (408, 503) are the resilience handler's job.

Cache-aside: consult the local cache or SQLite store before hitting the API.

## Navigation

Shell only. An `INavigationService` wraps `Shell.Current.GoToAsync` so ViewModels stay
testable. Register routes in `AppShell.xaml.cs`. Incoming navigation data arrives through
`IQueryAttributable` or `[QueryProperty]`.

## Settings

`Preferences.Get` does not appear in a ViewModel. An `ISettingsService` wraps
`Microsoft.Maui.Storage.Preferences`; the ViewModel takes the interface.

## Authentication

OIDC/OAuth2 with PKCE. Entra ID is the primary identity provider, via
Microsoft.Identity.Client (MSAL.NET) so the native broker is available.
`IdentityModel.OidcClient` is the fallback only when the provider is a generic OIDC host.

Tokens live in `ISecureStorage`, reached through `ISettingsService` — never in
Preferences, never in a literal.

## Testability

ViewModels take interfaces in the constructor. They never instantiate a service.
Tests mock `INavigationService` and data services; this standard only requires that the
code be mockable. Test ViewModels and services, not Views.

## Completion

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
