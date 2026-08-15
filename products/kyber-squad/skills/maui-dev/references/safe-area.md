---
name: maui-dev/safe-area
description: .NET MAUI safe area handling — SafeAreaEdges API (.NET 10), edge-to-edge layouts, keyboard avoidance, iOS notch, Android status/navigation bars.
source: https://github.com/dotnet/skills/tree/main/plugins/dotnet-maui/skills/maui-safe-area
---

# Safe Area — .NET MAUI

## What Is the Safe Area

The "safe area" is the screen region not obscured by the iOS notch/Dynamic Island, Android status bar, home indicator, or navigation bar. Content drawn outside the safe area is hidden or clipped by hardware.

---

## .NET 10 — SafeAreaEdges API (Breaking Change)

In .NET 10, `ContentPage` defaults to `SafeAreaEdges.None` (edge-to-edge). Previously pages had automatic insets.

**To restore the old behavior on a single page:**

```xml
<ContentPage
    Shell.SafeAreaEdges="All">
```

**To restore globally in `App.xaml`:**

```xml
<Style TargetType="ContentPage">
    <Setter Property="Shell.SafeAreaEdges" Value="All" />
</Style>
```

**Enum values:**

| Value | Inset applied |
|---|---|
| `None` | No automatic insets (edge-to-edge, .NET 10 default) |
| `Top` | Status bar / notch only |
| `Bottom` | Home indicator / Android nav bar only |
| `Left` | Left edge (landscape with notch) |
| `Right` | Right edge (landscape with notch) |
| `All` | All four edges (old .NET 9 default behavior) |

---

## Padding vs. SafeAreaEdges

**`Shell.SafeAreaEdges`** is for content you want kept entirely within the hardware-visible area.

**`Shell.SafeAreaInsets`** exposes the current inset values as a `Thickness` so you can apply them manually:

```csharp
var insets = Shell.Current.GetSafeAreaInsets();
MyContent.Margin = new Thickness(0, insets.Top, 0, insets.Bottom);
```

Use manual margins when you want content to extend under the notch/home indicator as a decorative background, but still keep interactive elements clear.

---

## Edge-to-Edge Immersive Layouts

For hero images, full-bleed headers, or map views that should bleed under the status bar:

```xml
<ContentPage Shell.SafeAreaEdges="Bottom">
    <!-- Image bleeds under status bar; only bottom inset applied -->
    <Grid>
        <Image Source="hero.jpg" Aspect="AspectFill" />
        <StackLayout VerticalOptions="End" Padding="16">
            <Label Text="Title" TextColor="White" />
        </StackLayout>
    </Grid>
</ContentPage>
```

---

## Keyboard Avoidance

On iOS and Android, the software keyboard can overlap inputs. MAUI handles this automatically for pages inside a `ScrollView`. For custom layouts:

```xml
<!-- Wrap form in ScrollView so MAUI can scroll it above the keyboard -->
<ScrollView>
    <StackLayout Padding="16">
        <Entry Placeholder="Name" />
        <Entry Placeholder="Email" Keyboard="Email" />
        <Button Text="Submit" Command="{Binding SubmitCommand}" />
    </StackLayout>
</ScrollView>
```

For more control, use `KeyboardOverlapBehavior` from `CommunityToolkit.Maui`:

```xml
<ContentPage>
    <toolkit:KeyboardObserverBehavior.KeyboardOverlapBehavior>
        <toolkit:KeyboardOverlapBehavior />
    </toolkit:KeyboardObserverBehavior.KeyboardOverlapBehavior>
</ContentPage>
```

---

## Android: Enforcing Edge-to-Edge

On Android API 35+, edge-to-edge is enforced by the OS. In `MainActivity.cs`:

```csharp
protected override void OnCreate(Bundle? savedInstanceState)
{
    base.OnCreate(savedInstanceState);
    // Ensure window insets are reported correctly
    WindowCompat.SetDecorFitsSystemWindows(Window!, false);
}
```

Without this call, the Android window may not report insets correctly to MAUI, causing content to appear behind system bars on older API levels.

---

## Common Pitfalls

| Issue | Fix |
|---|---|
| Content cut off under notch after upgrading to .NET 10 | Add `Shell.SafeAreaEdges="All"` to page or global style |
| Bottom button hidden by home indicator | Use `SafeAreaEdges="Bottom"` or add `Shell.GetSafeAreaInsets().Bottom` to padding |
| Keyboard overlaps form fields | Wrap content in `ScrollView` |
| Android status bar overlap | Add `WindowCompat.SetDecorFitsSystemWindows(Window!, false)` in `MainActivity.OnCreate` |

---

## References

- [Safe area on iOS](https://learn.microsoft.com/dotnet/maui/ios/platform-specifics/page-safe-area-layout)
- [Soft input mode (Android)](https://learn.microsoft.com/dotnet/maui/android/platform-specifics/soft-keyboard-input-mode)
- [.NET 10 safe area breaking change](https://learn.microsoft.com/dotnet/maui/whats-new/dotnet-10#safe-area-edges)
