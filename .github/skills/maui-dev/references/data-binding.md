---
name: maui-dev/data-binding
description: .NET MAUI data binding — compiled bindings, CommunityToolkit.Mvvm ObservableObject, threading rules.
source: https://github.com/dotnet/skills/tree/main/plugins/dotnet-maui/skills/maui-data-binding
---

# Data Binding — .NET MAUI

## Compiled Bindings (Preferred)

Compiled bindings are 8–20× faster than reflection-based bindings. Always use `x:DataType` on the page root and inside `DataTemplate`.

```xml
<!-- Page root -->
<ContentPage x:DataType="viewmodels:HomeViewModel">
    <Label Text="{Binding Title}" />
</ContentPage>

<!-- Inside DataTemplate — set x:DataType on the template, not child elements -->
<CollectionView.ItemTemplate>
    <DataTemplate x:DataType="models:Item">
        <Label Text="{Binding Name}" />
    </DataTemplate>
</CollectionView.ItemTemplate>
```

**Do NOT scatter `x:DataType="x:Object"` on child elements** — this re-enables reflection and defeats compile-time checking.

**Enable these as build errors** in your `.csproj`:

```xml
<PropertyGroup>
    <MSBuildWarningsAsErrors>XC0022;XC0025</MSBuildWarningsAsErrors>
</PropertyGroup>
```

---

## CommunityToolkit.Mvvm — Preferred MVVM Pattern

Use source-generated properties rather than manually implementing `INotifyPropertyChanged`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

public partial class HomeViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = "Home";

    [ObservableProperty]
    private bool _isLoading;

    [RelayCommand]
    private async Task LoadDataAsync()
    {
        IsLoading = true;
        try { /* load */ }
        finally { IsLoading = false; }
    }
}
```

Source generation produces `Title`, `IsLoading` properties and `LoadDataCommand` automatically.

---

## Setting BindingContext

**Preferred — constructor injection with DI:**

```csharp
public partial class HomePage : ContentPage
{
    public HomePage(HomeViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
```

**XAML only (no DI):**

```xml
<ContentPage.BindingContext>
    <viewmodels:HomeViewModel />
</ContentPage.BindingContext>
```

---

## Binding Modes

Omit `Mode` when using the control's default — only specify when overriding:

| Scenario | Mode |
|---|---|
| Read-only display | `OneWay` (default for most) |
| Two-way form field | `TwoWay` |
| One-time static data | `OneTime` |
| ViewModel reads UI value | `OneWayToSource` |

```xml
<Entry Text="{Binding SearchTerm, Mode=TwoWay}" />
```

---

## Advanced Patterns

### Relative Bindings

```xml
<!-- Bind to own property -->
<Label FontSize="{Binding Source={RelativeSource Self}, Path=Width}" />

<!-- Reach ancestor ViewModel from inside DataTemplate -->
<Button Command="{Binding Source={RelativeSource AncestorType={x:Type ContentPage}},
                          Path=BindingContext.DeleteCommand}"
        CommandParameter="{Binding}" />
```

### String Formatting

```xml
<Label Text="{Binding Price, StringFormat='Price: {0:C2}'}" />
<Label Text="{Binding CreatedAt, StringFormat='{0:MMM dd, yyyy}'}" />
```

### Null/Fallback Values

```xml
<Label Text="{Binding Nickname, TargetNullValue='Unknown', FallbackValue='Loading...'}" />
```

### .NET 9+ AOT-Safe Code Bindings (no reflection)

```csharp
myLabel.SetBinding(Label.TextProperty, static (HomeViewModel vm) => vm.Title);
```

---

## Threading Rule

MAUI automatically marshals `PropertyChanged` notifications to the UI thread for simple property changes. However, **direct `ObservableCollection<T>` mutations from a background thread require explicit dispatch**:

```csharp
await Task.Run(async () =>
{
    var data = await FetchDataAsync();
    MainThread.BeginInvokeOnMainThread(() =>
    {
        foreach (var item in data) Items.Add(item);
    });
});
```

---

## References

- [Data binding overview](https://learn.microsoft.com/dotnet/maui/fundamentals/data-binding/)
- [Compiled bindings](https://learn.microsoft.com/dotnet/maui/fundamentals/data-binding/compiled-bindings)
- [CommunityToolkit.Mvvm](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/)
