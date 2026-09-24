---
name: maui-dev/collectionview
description: .NET MAUI CollectionView — layouts, selection, grouping, pull-to-refresh, incremental loading, SwipeView, EmptyView, and performance.
source: https://github.com/dotnet/skills/tree/main/plugins/dotnet-maui/skills/maui-collectionview
---

# CollectionView — .NET MAUI

## Basic Setup

```xml
<CollectionView ItemsSource="{Binding Items}"
                SelectionMode="Single"
                SelectedItem="{Binding SelectedItem, Mode=TwoWay}">
    <CollectionView.ItemTemplate>
        <DataTemplate x:DataType="models:Item">
            <Grid Padding="10">
                <Label Text="{Binding Name}" />
            </Grid>
        </DataTemplate>
    </CollectionView.ItemTemplate>
</CollectionView>
```

**Always set `x:DataType` on the `DataTemplate`** — enables compiled bindings and catches typos at build time.

---

## Layouts

### Vertical List (default)

```xml
<CollectionView ItemsLayout="VerticalList" />
```

### Horizontal Scroll

```xml
<CollectionView ItemsLayout="HorizontalList" />
```

### Grid

```xml
<CollectionView>
    <CollectionView.ItemsLayout>
        <GridItemsLayout Orientation="Vertical" Span="2" />
    </CollectionView.ItemsLayout>
</CollectionView>
```

---

## Selection

| Mode | Behavior |
|---|---|
| `None` | No selection, `SelectedItem` always null |
| `Single` | One item at a time; `SelectedItem` updates |
| `Multiple` | Multiple items; use `SelectedItems` collection |

```xml
<CollectionView SelectionMode="Single"
                SelectedItem="{Binding SelectedItem, Mode=TwoWay}"
                SelectionChanged="OnSelectionChanged">
```

**Do NOT use both `SelectedItem` binding and a `SelectionChanged` handler** for navigation — pick one. Mixing both causes double-execution on tap.

---

## Grouping

```csharp
// ViewModel
public ObservableCollection<GroupedItems<string, Item>> GroupedItems { get; } = new();

// Populate
var groups = items
    .GroupBy(i => i.Category)
    .Select(g => new GroupedItems<string, Item>(g.Key, g))
    .ToList();
```

```xml
<CollectionView ItemsSource="{Binding GroupedItems}"
                IsGrouped="True">
    <CollectionView.GroupHeaderTemplate>
        <DataTemplate x:DataType="x:String">
            <Label Text="{Binding}" FontAttributes="Bold" />
        </DataTemplate>
    </CollectionView.GroupHeaderTemplate>
    <CollectionView.ItemTemplate>
        <DataTemplate x:DataType="models:Item">
            <Label Text="{Binding Name}" />
        </DataTemplate>
    </CollectionView.ItemTemplate>
</CollectionView>
```

---

## Pull-to-Refresh

```xml
<RefreshView Command="{Binding RefreshCommand}"
             IsRefreshing="{Binding IsRefreshing}">
    <CollectionView ItemsSource="{Binding Items}">
        <!-- templates -->
    </CollectionView>
</RefreshView>
```

```csharp
[RelayCommand]
private async Task RefreshAsync()
{
    IsRefreshing = true;
    await LoadDataAsync();
    IsRefreshing = false;
}
```

---

## Incremental Loading (RemainingItemsThreshold)

```xml
<CollectionView ItemsSource="{Binding Items}"
                RemainingItemsThreshold="5"
                RemainingItemsThresholdReachedCommand="{Binding LoadMoreCommand}">
```

```csharp
[RelayCommand]
private async Task LoadMoreAsync()
{
    if (_isLoadingMore || !HasMore) return;
    _isLoadingMore = true;
    var next = await _api.GetItemsAsync(page: ++_page);
    foreach (var item in next) Items.Add(item);
    _isLoadingMore = true; // reset flag
}
```

**Guard with `_isLoadingMore` flag** — `RemainingItemsThresholdReached` fires multiple times while the user scrolls through the threshold band.

---

## SwipeView for Contextual Actions

```xml
<CollectionView.ItemTemplate>
    <DataTemplate x:DataType="models:Item">
        <SwipeView>
            <SwipeView.RightItems>
                <SwipeItems>
                    <SwipeItem Text="Delete"
                               BackgroundColor="Red"
                               Command="{Binding Source={RelativeSource AncestorType={x:Type ContentPage}},
                                                  Path=BindingContext.DeleteCommand}"
                               CommandParameter="{Binding}" />
                </SwipeItems>
            </SwipeView.RightItems>
            <!-- actual item content -->
            <Grid Padding="10">
                <Label Text="{Binding Name}" />
            </Grid>
        </SwipeView>
    </DataTemplate>
</CollectionView.ItemTemplate>
```

Use `RelativeSource AncestorType` to reach the page's ViewModel `DeleteCommand` from inside the `DataTemplate`.

---

## EmptyView

```xml
<CollectionView ItemsSource="{Binding Items}">
    <CollectionView.EmptyView>
        <StackLayout>
            <Label Text="No results found" HorizontalOptions="Center" />
        </StackLayout>
    </CollectionView.EmptyView>
</CollectionView>
```

For state-dependent empty views (loading vs. empty) use `EmptyViewTemplate` with a `DataTemplateSelector`.

---

## Performance Tips

| Tip | Why |
|---|---|
| Always use `x:DataType` in `DataTemplate` | Compiled bindings skip reflection per-cell |
| Avoid `ObservableCollection` bulk mutations in loop | Each `Add` fires UI update — use `AddRange` extension or reset |
| Keep cell layouts flat (avoid deep nesting) | Measure/layout passes are per-cell and expensive |
| Don't set `HasUnevenRows` unless cells vary | Uniform row heights skip the measure pass |
| Use `RemainingItemsThreshold` over `Scrolled` event | Event fires every pixel; threshold only fires once per page |

---

## References

- [CollectionView overview](https://learn.microsoft.com/dotnet/maui/user-interface/controls/collectionview/)
- [CollectionView grouping](https://learn.microsoft.com/dotnet/maui/user-interface/controls/collectionview/grouping)
- [SwipeView](https://learn.microsoft.com/dotnet/maui/user-interface/controls/swipeview)
- [RefreshView](https://learn.microsoft.com/dotnet/maui/user-interface/controls/refreshview)
