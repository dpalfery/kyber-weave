---
name: maui-dev/app-lifecycle
description: .NET MAUI app lifecycle — window states, lifecycle events, state preservation, and Android back-button handling.
source: https://github.com/dotnet/skills/tree/main/plugins/dotnet-maui/skills/maui-app-lifecycle
---

# App Lifecycle — .NET MAUI

## Window States

```
Created → Resumed → Deactivated → Stopped → Destroyed
                 ↑_______________↓ (returning from background)
```

| State | Meaning | User action |
|---|---|---|
| Created | Window instance exists, not yet visible | App launch |
| Resumed | App in foreground, interactive | Restored from background; initial focus |
| Deactivated | Partially visible or losing focus | Multitasking overlay, incoming call |
| Stopped | Not visible; may be kept in memory | Swiped to background |
| Destroyed | Removed from memory | OS killed; user swiped away |

---

## Lifecycle Events

Override in `App.xaml.cs` or subscribe via `Window`:

```csharp
public partial class App : Application
{
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = base.CreateWindow(activationState);

        window.Created  += (s, e) => { /* first-time setup */ };
        window.Resumed  += (s, e) => { /* re-fetch stale data, restart timers */ };
        window.Deactivated += (s, e) => { /* pause non-critical work */ };
        window.Stopped  += (s, e) => SaveStateAsync(); // see below
        window.Destroying += (s, e) => { /* release unmanaged resources */ };

        return window;
    }
}
```

**Do NOT start heavy async work in `Resumed` synchronously.** Use `Task.Run` or `_ = LoadAsync()` — event handlers that block the UI thread will cause ANR on Android.

---

## Page-Level Lifecycle

Override `OnAppearing` / `OnDisappearing` in `ContentPage` for per-page loading:

```csharp
protected override async void OnAppearing()
{
    base.OnAppearing();
    await ViewModel.LoadAsync();
}

protected override void OnDisappearing()
{
    base.OnDisappearing();
    ViewModel.CancelPendingOperations();
}
```

`OnAppearing` is called every time the page becomes visible — including Back navigation. Guard against redundant loads with a `_loaded` flag when load is expensive.

---

## State Preservation

Save UI state before `Stopped`; restore in `Resumed`:

```csharp
private async void SaveStateAsync()
{
    // Use Preferences for lightweight key-value state
    Preferences.Set("last_search", ViewModel.SearchTerm);
    Preferences.Set("selected_id", ViewModel.SelectedItem?.Id ?? 0);
}

protected override async void OnResumed()
{
    base.OnResumed();
    var term = Preferences.Get("last_search", string.Empty);
    if (!string.IsNullOrEmpty(term))
        await ViewModel.RestoreStateAsync(term);
}
```

For complex state, serialize to JSON in `FileSystem.AppDataDirectory`.

---

## Android Back Button

On Android, the hardware/gesture back button triggers `OnBackButtonPressed`. Override to intercept:

```csharp
protected override bool OnBackButtonPressed()
{
    if (ViewModel.HasUnsavedChanges)
    {
        // Intercept — show confirmation dialog asynchronously
        _ = ConfirmExitAsync();
        return true;  // true = handled, suppress default back behavior
    }

    return base.OnBackButtonPressed(); // false = allow OS to handle
}

private async Task ConfirmExitAsync()
{
    bool exit = await DisplayAlert("Unsaved Changes", "Discard?", "Yes", "No");
    if (exit) await Shell.Current.GoToAsync("..");
}
```

**This does NOT fire on iOS/MacCatalyst** — use Shell navigation guards (`OnNavigating` + `GetDeferral`) for cross-platform unsaved-change protection.

---

## Platform-Specific Hooks

For platform code that must run at specific lifecycle points:

```csharp
// Platforms/Android/MainActivity.cs
protected override void OnPause()
{
    base.OnPause();
    // Android-only: pause sensors, camera, etc.
}
```

Keep platform lifecycle handlers thin — delegate business logic to a shared service.

---

## Common Pitfalls

| Issue | Fix |
|---|---|
| Timers / subscriptions not paused when backgrounded | Stop in `Stopped`, restart in `Resumed` |
| `OnAppearing` reload hammers the API on every Back nav | Guard with `_loaded` flag or use `IQueryAttributable` to detect fresh navigations |
| Android back captured by page but not Shell nav guard | Use both `OnBackButtonPressed` (Android) AND `OnNavigating` guard for full coverage |
| Preferences flushed on force-quit | Don't rely on `Stopped` for critical data — persist incrementally on ViewModel changes |

---

## References

- [App lifecycle](https://learn.microsoft.com/dotnet/maui/fundamentals/app-lifecycle)
- [Window lifecycle events](https://learn.microsoft.com/dotnet/maui/fundamentals/windows)
- [Preferences API](https://learn.microsoft.com/dotnet/maui/platform-integration/storage/preferences)
