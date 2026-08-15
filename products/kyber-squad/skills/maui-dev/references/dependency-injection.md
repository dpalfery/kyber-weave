---
name: maui-dev/dependency-injection
description: Dependency injection in .NET MAUI — MauiProgram registration, Shell integration, lifetime rules, and common pitfalls.
source: https://github.com/dotnet/skills/tree/main/plugins/dotnet-maui/skills/maui-dependency-injection
---

# Dependency Injection — .NET MAUI

## Registration in MauiProgram.cs

All services, ViewModels, and Pages register on `builder.Services` in `MauiProgram.CreateMauiApp()`:

```csharp
public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        // Services — Singleton for shared, expensive-to-create resources
        builder.Services.AddSingleton<IHttpClientFactory, HttpClientFactory>();
        builder.Services.AddSingleton<IMotorcycleApiClient, MotorcycleApiClient>();

        // Pages — Transient so each navigation gets a fresh instance
        builder.Services.AddTransient<HomePage>();
        builder.Services.AddTransient<SearchPage>();
        builder.Services.AddTransient<ManualDetailPage>();

        // ViewModels — Transient to avoid stale state between navigations
        builder.Services.AddTransient<HomeViewModel>();
        builder.Services.AddTransient<SearchViewModel>();
        builder.Services.AddTransient<ManualDetailViewModel>();

        return builder.Build();
    }
}
```

---

## Lifetime Rules

| Lifetime | Use for | Risk if misused |
|---|---|---|
| `Singleton` | Shared services, caches, HttpClient factories | Retains state indefinitely — avoid for ViewModels |
| `Transient` | Pages, ViewModels, request-scoped work | Fresh instance per resolution — correct for most UI objects |
| `Scoped` | Rarely used in MAUI — requires manual `IServiceScope` management | Scoped lifetime doesn't map cleanly to navigation lifetime |

**Never register a ViewModel as Singleton.** Singleton ViewModels retain state across navigations — a user going Back and then forward to the same screen sees stale data.

---

## Constructor Injection

Dependencies flow through constructor parameters automatically:

```csharp
// ViewModel receives service via injection
public class SearchViewModel : ObservableObject
{
    private readonly IMotorcycleApiClient _api;

    public SearchViewModel(IMotorcycleApiClient api)
    {
        _api = api;
    }
}

// Page receives ViewModel via injection
public partial class SearchPage : ContentPage
{
    public SearchPage(SearchViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
```

---

## Shell Navigation Integration

Pages registered in both DI and as Shell routes resolve their complete dependency graph automatically:

```csharp
// MauiProgram.cs
builder.Services.AddTransient<ManualDetailPage>();
builder.Services.AddTransient<ManualDetailViewModel>();

// AppShell.xaml.cs constructor
Routing.RegisterRoute(nameof(ManualDetailPage), typeof(ManualDetailPage));
```

When `GoToAsync(nameof(ManualDetailPage))` is called, Shell uses the DI container to resolve `ManualDetailPage`, which resolves `ManualDetailViewModel`, which resolves all its own dependencies — all automatically.

---

## Platform-Specific Registration

```csharp
#if ANDROID
builder.Services.AddSingleton<IPlatformService, AndroidPlatformService>();
#elif IOS || MACCATALYST
builder.Services.AddSingleton<IPlatformService, ApplePlatformService>();
#else
builder.Services.AddSingleton<IPlatformService, DefaultPlatformService>();
#endif
```

**Always include a fallback** (`#else`) to prevent null dependency injection on unexpected platforms.

---

## Common Pitfalls

| Pitfall | Fix |
|---|---|
| **Singleton ViewModel** retains stale data between navigations | Register ViewModels as `Transient` |
| **Unregistered Page**: Shell instantiates route types with `ActivatorUtilities.GetServiceOrCreateInstance` and resolves dependencies; transient registration is standard | Register pages as `Transient` in `builder.Services` for explicit lifetime management and reliable dependency resolution |
| **Service-dependent XAML resources** (e.g., converters) execute before container is ready | Defer container-dependent work to `CreateWindow()` or `OnAppearing()` |
| **Service Locator pattern** (`ServiceProvider.GetService<T>()` in ViewModel) | Use constructor injection — hides dependencies and complicates testing |
| **Missing platform branch** in conditional registration | Always add `#else` with a default or throw to surface the gap |

---

## Testing

Register test doubles in place of real services for unit testing ViewModels:

```csharp
// In tests — don't use MauiProgram; build a minimal container
var services = new ServiceCollection();
services.AddTransient<SearchViewModel>();
services.AddSingleton<IMotorcycleApiClient>(Substitute.For<IMotorcycleApiClient>());
var provider = services.BuildServiceProvider();
var vm = provider.GetRequiredService<SearchViewModel>();
```

---

## References

- [.NET MAUI dependency injection](https://learn.microsoft.com/dotnet/maui/fundamentals/dependency-injection)
- [Register routes with dependency injection](https://learn.microsoft.com/dotnet/maui/fundamentals/shell/navigation#register-routes)
