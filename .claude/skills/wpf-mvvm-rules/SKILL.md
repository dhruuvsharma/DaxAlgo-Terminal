---
name: wpf-mvvm-rules
description: MVVM patterns, threading rules, and C# code style preferences for DaxAlgo Terminal. Use when writing or editing view-models, code-behind, observable collections, or anything involving async/Dispatcher in the UI layer. Also covers nullable/internal/file-scoped namespace conventions. Skip for pure Core or Infrastructure work that doesn't touch UI.
---

# WPF / MVVM Rules

## MVVM strictness

- **No business logic in `.xaml.cs`.** Code-behind exists for view-only concerns (e.g. focus, animation hookup). Anything testable goes in the view-model.
- View-models inherit `ViewModelBase`.
- Use `CommunityToolkit.Mvvm` source generators: `[ObservableProperty]` for backing fields, `[RelayCommand]` for commands. Don't write `OnPropertyChanged` by hand unless you're doing something unusual.
- `[ObservableProperty]` on `private string _foo` generates `public string Foo { get; set; }` with INotifyPropertyChanged. Bind to `Foo`, not `_foo`.
- `[RelayCommand]` on `private void DoX()` generates `DoXCommand`. Bind to `DoXCommand`.

## Threading rules

- **Broker callbacks marshal to the UI dispatcher inside `MarketDataRepository`.** Not in the broker client. Not in the view-model. View-models stay single-threaded from their POV.
- **No `Dispatcher.Invoke` in view-models.** If you find yourself reaching for it, the repository layer is wrong.
- **No `ConfigureAwait(false)` in view-model code.** We *want* to resume on the UI thread.
- **Use `ConfigureAwait(false)` in pure-library code** (anything in `Core/` or non-UI `Infrastructure/`).
- **Async streams**: `IAsyncEnumerable<Bar>` / `IAsyncEnumerable<Tick>` with `[EnumeratorCancellation]` on the `CancellationToken` parameter. Cancellation is the natural unsubscribe path. Don't add a `StopAsync()` alongside.

## Observability

- Connection state flows as `IObservable<ConnectionState>` from `ConnectionManager` → repository → view-models. Don't poll.
- Logs flow through Serilog with a custom in-memory sink (the Logs pane).
- Notifications go through `INotificationPublisher` — see [add-notifier](../add-notifier/SKILL.md).

## Code style

- **File-scoped namespaces.** `namespace Foo;` not `namespace Foo { }`.
- **`internal` by default**, `public` only at module boundaries. New types in `Core/` exposed to other projects are public; everything else starts internal.
- **No comments** unless the *why* is non-obvious. Identifiers explain *what*. If you write a comment, it's because of a hidden constraint, a workaround, or surprising behavior.
- **No defensive null-checks on internal calls.** Trust the type system. Validate at boundaries: config load, broker callbacks, user input. Nullable reference types are enabled — let the compiler do the work.
- **Records for value types** (`Bar`, `Tick`, `Contract`). Sealed by default.
- **Prefer `IReadOnlyList<T>`** over `List<T>` in public signatures.

## XAML / AvalonDock quirks

- AvalonDock VS2013 Dark theme is the shell. New docked panels go through `DockingManager`.
- **Binding errors fail silently** inside AvalonDock layouts. If a binding "isn't working", check the Output window for `BindingExpression path error` lines before assuming the data is wrong.
- MahApps Metro chrome handles the window frame — don't override `WindowStyle` / `WindowChrome` without checking the existing Mahapps styles.
- ScottPlot 5 candlestick chart on the right pane — auto-scrolling, last ~200 bars, configurable timeframe. Don't reach for OxyPlot / LiveCharts.

## When debugging XAML or bindings

- Spawn the `xaml-fixer` subagent for targeted XAML/binding/style fixes.
- For "where is this resource / view-model defined" lookups, use the `wpf-explorer` subagent (Haiku, cheap).

## Hard rules

- **No `Dispatcher.Invoke` in view-models.**
- **No business logic in `.xaml.cs`.**
- **No `new`-ing strategies or broker clients from the shell** — always through factories / DI.
- **No emojis** in code or comments unless explicitly requested.
