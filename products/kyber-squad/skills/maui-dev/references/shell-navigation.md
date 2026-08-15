---
name: maui-dev/shell-navigation
description: .NET MAUI Shell navigation — GoToAsync, route registration, data transfer, and navigation guards.
source: https://github.com/dotnet/skills/tree/main/plugins/dotnet-maui/skills/maui-shell-navigation
---

# Shell Navigation — .NET MAUI

## Visual Hierarchy

```text
Shell
└── FlyoutItem / TabBar
    └── Tab
        └── ShellContent → ContentPage
```

Always use `ContentTemplate` with `DataTemplate` for on-demand page loading (not `Content`). Using `Content` eagerly instantiates every page at startup and hurts launch performance.

---

## Route Registration

Register detail pages (not in the Shell visual hierarchy) in the `AppShell` constructor:

```csharp
public AppShell()
{
    InitializeComponent();
    Routing.RegisterRoute(nameof(DetailPage), typeof(DetailPage));
    Routing.RegisterRoute("product/detail", typeof(ProductDetailPage));
}
```

**Never register duplicate route names** — causes unpredictable navigation behavior.

---

## Navigation

```csharp
// Absolute — navigate to root-level Shell item
await Shell.Current.GoToAsync("//home");

// Relative — push a detail page
await Shell.Current.GoToAsync(nameof(DetailPage));

// Back
await Shell.Current.GoToAsync("..");

// Back two levels
await Shell.Current.GoToAsync("../..");
```

**Always `await` GoToAsync.** Omitting `await` causes race conditions where the page isn't ready when you try to pass data.

---

## Passing Data

### Preferred: IQueryAttributable on ViewModel

```csharp
public class DetailViewModel : IQueryAttributable
{
    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("id", out var id))
            Id = (int)id;
    }
}

// Navigate with parameters
await Shell.Current.GoToAsync(nameof(DetailPage), new Dictionary<string, object>
{
    { "id", selectedItem.Id }
});
```

### Alternative: QueryProperty attribute on Page

```csharp
[QueryProperty(nameof(ItemId), "id")]
public partial class DetailPage : ContentPage
{
    public int ItemId { get; set; }
}
```

### Complex objects: ShellNavigationQueryParameters

```csharp
await Shell.Current.GoToAsync(nameof(DetailPage),
    new ShellNavigationQueryParameters { { "item", selectedItem } });
```

---

## Navigation Guards

Use for unsaved-change warnings, auth checks, etc.:

```csharp
protected override void OnNavigating(ShellNavigatingEventArgs args)
{
    base.OnNavigating(args);

    if (HasUnsavedChanges && args.CanCancel)
    {
        var deferral = args.GetDeferral();  // MUST call GetDeferral for async guards
        _ = ConfirmNavigationAsync(args, deferral);
    }
}

private async Task ConfirmNavigationAsync(ShellNavigatingEventArgs args, ShellNavigatingDeferral deferral)
{
    bool confirmed = await DisplayAlert("Unsaved changes", "Discard?", "Yes", "No");
    if (!confirmed) args.Cancel();
    deferral.Complete();
}
```

**Always call `GetDeferral()` before any async operation in navigation guards.** Without it, navigation completes before your async check runs.

---

## Common Pitfalls

| Issue | Fix |
|---|---|
| Page instantiated at startup | Use `ContentTemplate`+`DataTemplate`, not `Content` |
| Race condition on navigation | Always `await GoToAsync()` |
| Duplicate route name crash | Use unique route strings; check before adding |
| Data not received on target page | Implement `IQueryAttributable`; check route parameter key spelling |
| Async guard doesn't cancel | Call `GetDeferral()` before any await |

---

## References

- [Shell overview](https://learn.microsoft.com/dotnet/maui/fundamentals/shell/)
- [Shell navigation](https://learn.microsoft.com/dotnet/maui/fundamentals/shell/navigation)
- [Pass data with query parameters](https://learn.microsoft.com/dotnet/maui/fundamentals/shell/navigation#pass-data)
