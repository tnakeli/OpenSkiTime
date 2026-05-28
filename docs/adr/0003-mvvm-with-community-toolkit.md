# ADR 0003 — MVVM with CommunityToolkit.Mvvm

**Date**: 2026-05-28  
**Status**: Accepted  
**Context**: Choosing an MVVM implementation strategy for the Avalonia desktop UI.

## Decision

Use **CommunityToolkit.Mvvm 8.x** source generators (`[ObservableProperty]`, `[RelayCommand]`, `ObservableObject`) for all ViewModels.

## Rationale

- **Source generators** eliminate boilerplate `INotifyPropertyChanged` implementations at compile time — no runtime reflection.
- **`[RelayCommand]`** auto-generates async commands with `CanExecute` support from a single method attribute.
- **`[NotifyCanExecuteChangedFor]`** keeps command availability in sync with property changes without manual wiring.
- The toolkit is maintained by Microsoft, ships with stable Avalonia integration, and is the community standard for .NET MVVM.

## Consequences

- All ViewModels inherit `ObservableObject` (or `ViewModelBase : ObservableObject`).
- Properties exposed to XAML bindings are annotated with `[ObservableProperty]`.
- Commands are `[RelayCommand]` methods; async commands return `Task`.
- ViewModels are registered in `Program.cs`; Views receive their VM via Avalonia's `DataContext`.
- No `ReactiveUI` or `Prism` dependency; if reactive streams are needed in future, they can be added alongside without conflict.
