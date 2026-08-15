---
name: maui-dev/theming
description: .NET MAUI theming — AppThemeBinding, ResourceDictionary, DynamicResource, light/dark mode, and platform color integration.
source: https://github.com/dotnet/skills/tree/main/plugins/dotnet-maui/skills/maui-theming
---

# Theming — .NET MAUI

## AppThemeBinding (Declarative Light/Dark)

Apply different values per OS theme in XAML without code:

```xml
<ContentPage
    BackgroundColor="{AppThemeBinding Light={StaticResource PageBackgroundLight}, Dark={StaticResource PageBackgroundDark}}">

    <ContentPage.Resources>
        <ResourceDictionary>
            <Color x:Key="PageBackgroundLight">#FFFFFF</Color>
            <Color x:Key="PageBackgroundDark">#121212</Color>
            <Color x:Key="TextLight">#1C1C1C</Color>
            <Color x:Key="TextDark">#FAFAFA</Color>
            <Color x:Key="CardLight">#F5F5F5</Color>
            <Color x:Key="CardDark">#1E1E1E</Color>
        </ResourceDictionary>
    </ContentPage.Resources>

    <Label Text="Hello"
           TextColor="{AppThemeBinding Light={StaticResource TextLight}, Dark={StaticResource TextDark}}"
           BackgroundColor="{AppThemeBinding Light={StaticResource CardLight}, Dark={StaticResource CardDark}}" />
</ContentPage>
```

**Use `StaticResource` references inside `AppThemeBinding`** — don't embed hex codes directly; centralizing colors in a `ResourceDictionary` makes theme updates a one-place change.

---

## ResourceDictionary Setup

Define semantic color tokens in `App.xaml`:

```xml
<Application.Resources>
    <ResourceDictionary>
        <!-- Light tokens -->
        <Color x:Key="PrimaryLight">#0078D4</Color>
        <Color x:Key="SurfaceLight">#FFFFFF</Color>
        <Color x:Key="OnSurfaceLight">#1C1C1C</Color>

        <!-- Dark tokens -->
        <Color x:Key="PrimaryDark">#60CDFF</Color>
        <Color x:Key="SurfaceDark">#1C1C1C</Color>
        <Color x:Key="OnSurfaceDark">#FAFAFA</Color>

        <!-- Merged theme dictionaries -->
        <ResourceDictionary.MergedDictionaries>
            <ResourceDictionary Source="Styles/Typography.xaml" />
            <ResourceDictionary Source="Styles/Controls.xaml" />
        </ResourceDictionary.MergedDictionaries>
    </ResourceDictionary>
</Application.Resources>
```

---

## DynamicResource (Runtime Theme Switching)

`StaticResource` resolves once at load time — use `DynamicResource` when you want controls to update when the dictionary changes at runtime:

```xml
<!-- Will update if the dictionary entry changes at runtime -->
<Label TextColor="{DynamicResource PrimaryColor}" />
```

**Use `DynamicResource` for any color you expect to change while the app is running** (e.g., manual theme toggle). Use `StaticResource` for values that never change after the page loads.

---

## Programmatic Theme Toggle

```csharp
public void SetTheme(AppTheme theme)
{
    Application.Current!.UserAppTheme = theme;
}

// Light / dark / follow OS
SetTheme(AppTheme.Light);
SetTheme(AppTheme.Dark);
SetTheme(AppTheme.Unspecified); // follow OS
```

Persist the preference with `Preferences.Set("app_theme", (int)theme)` and restore it in `App.xaml.cs` constructor.

---

## Reading Current Theme in Code

```csharp
var isDark = Application.Current!.RequestedTheme == AppTheme.Dark;
```

Subscribe to changes:

```csharp
Application.Current!.RequestedThemeChanged += (s, e) =>
{
    UpdateThemeDependent(e.RequestedTheme);
};
```

---

## Android: Prevent Activity Restart on Theme Change

Without this, rotating the device or toggling dark mode recreates the Android `Activity` and loses in-flight state:

```csharp
// Platforms/Android/MainActivity.cs
[Activity(ConfigurationChanges = ConfigChanges.ScreenSize
                               | ConfigChanges.Orientation
                               | ConfigChanges.UiMode)]
public class MainActivity : MauiAppCompatActivity { }
```

**`ConfigChanges.UiMode` is required** — without it, every system dark/light toggle triggers a full Activity restart.

---

## Semantic Colors (Recommended Pattern)

Define use-case tokens (not raw palette entries) so the theme switch is automatic:

```xml
<!-- In App.xaml -->
<Color x:Key="PageBackground">{AppThemeBinding Light=#FFFFFF, Dark=#121212}</Color>
<Color x:Key="CardSurface">{AppThemeBinding Light=#F5F5F5, Dark=#1E1E1E}</Color>
<Color x:Key="PrimaryText">{AppThemeBinding Light=#1C1C1C, Dark=#FAFAFA}</Color>
<Color x:Key="AccentColor">{AppThemeBinding Light=#0078D4, Dark=#60CDFF}</Color>
```

Then controls reference `PageBackground` not a raw hex — when the OS switches, everything updates.

---

## Implicit Styles

Apply styles automatically to all controls of a given type without `x:Key`:

```xml
<Style TargetType="Label">
    <Setter Property="TextColor" Value="{DynamicResource PrimaryText}" />
    <Setter Property="FontFamily" Value="OpenSansRegular" />
</Style>

<Style TargetType="Button">
    <Setter Property="BackgroundColor" Value="{DynamicResource AccentColor}" />
    <Setter Property="TextColor" Value="White" />
    <Setter Property="CornerRadius" Value="8" />
</Style>
```

---

## Common Pitfalls

| Issue | Fix |
|---|---|
| Color unchanged when OS toggles | Use `DynamicResource` not `StaticResource` for theme-responsive colors |
| Activity restarts on dark-mode toggle | Add `ConfigChanges.UiMode` to `MainActivity` attribute |
| Hard-coded hex in XAML | Replace with named `ResourceDictionary` entries |
| `AppThemeBinding` on Android API < 29 | Not supported — handle with `Application.Current.RequestedThemeChanged` |

---

## References

- [Theming overview](https://learn.microsoft.com/dotnet/maui/user-interface/theming)
- [AppThemeBinding](https://learn.microsoft.com/dotnet/maui/fundamentals/resource-dictionaries#consume-an-appthemebinding-markup-extension)
- [ResourceDictionary](https://learn.microsoft.com/dotnet/maui/fundamentals/resource-dictionaries)
- [System theme changes](https://learn.microsoft.com/dotnet/maui/user-interface/system-theme-changes)
