---
name: maui-dev
description: Use when writing .NET MAUI UI code, XAML pages, Shell navigation, MVVM/CommunityToolkit patterns, CollectionView, data binding, or cross-platform mobile/desktop features.
license: MIT
metadata:
  author: David R Palfery
  version: 1.0.0
---

# .NET MAUI Developer

Architecture and UI decisions live in the path declared as **<maui-coding-standard>**
in the repository root `AGENTS.md`. Language-level C# decisions live in the path
declared as **<csharp-coding-standard>**. Read those before the references below —
this skill is procedure, not policy.

Identify your sub-task and read ONLY the relevant reference before proceeding.

| Sub-Task | When to Use | Reference |
|---|---|---|
| Shell Navigation | GoToAsync, route registration, IQueryAttributable, navigation guards, back navigation | [Shell Navigation](./references/shell-navigation.md) |
| Data Binding | Compiled bindings, x:DataType, ObservableObject, MVVM, CommunityToolkit source generation | [Data Binding](./references/data-binding.md) |
| CollectionView | Scrollable lists/grids, selection, grouping, pull-to-refresh, incremental loading, SwipeView | [CollectionView](./references/collectionview.md) |
| Dependency Injection | MauiProgram DI setup, singleton/transient registration, Shell + DI integration, pitfalls | [Dependency Injection](./references/dependency-injection.md) |
| App Lifecycle | Window state transitions, OnStopped/OnResumed, state preservation, Android back-button | [App Lifecycle](./references/app-lifecycle.md) |
| Theming | AppThemeBinding, light/dark mode, ResourceDictionary swapping, DynamicResource | [Theming](./references/theming.md) |
| Safe Area | .NET 10 SafeAreaEdges API, edge-to-edge layouts, keyboard avoidance, iOS notch/Android status bar | [Safe Area](./references/safe-area.md) |
| Environment Doctor | Diagnose and fix MAUI SDK, workload, Android, Xcode setup issues | [Environment Doctor](./references/environment-doctor.md) |

**Rule:** Read only the reference(s) relevant to your current task. Do not pre-load all references.
