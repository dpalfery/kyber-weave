---
id: standards/maui/rules
title: "maui rules"
doc-type: rule
status: draft
technology: maui
owner: unassigned
last-reviewed: 2026-11-22
---

# maui rules

## Boundaries

- Consume the API contracts the backend exposes; do not implement server endpoints.
- ViewModels take interfaces rather than constructing services.

## Correctness

- Set `x:DataType` on every page root and `DataTemplate`; never `x:DataType="x:Object"` on children.
- Never register a ViewModel as Singleton; register pages and ViewModels Transient.
- Register every Shell route once, with a unique name, and `await` every `GoToAsync`.
- Async navigation guards call `GetDeferral()` before the first await.
- Never mutate an `ObservableCollection` from a background thread without dispatching to the main thread.
- Never block the UI thread in a lifecycle handler or a page `OnAppearing`.
- Android `MainActivity` keeps `ConfigChanges.UiMode`.

## Security and data handling

- Never edit signing keys or publish credentials.
