namespace OpenSkiTime.Desktop.Services;

/// <summary>
/// Abstraction over user-facing message / confirm / error dialogs. Concrete
/// Avalonia-based implementations live alongside; tests substitute fakes.
/// </summary>
public interface IDialogService
{
    Task ShowMessageAsync(string title, string message);

    Task ShowErrorAsync(string title, string message);

    Task<bool> ConfirmAsync(string title, string message);
}

/// <summary>
/// Trivial Phase-2 implementation. Real Avalonia dialog rendering arrives
/// alongside the screens that need it (Phase 3 onward).
/// </summary>
public sealed class DialogService : IDialogService
{
    public Task ShowMessageAsync(string title, string message)
    {
        // TODO(Phase 3): replace with Avalonia dialog window.
        Console.WriteLine($"[INFO] {title}: {message}");
        return Task.CompletedTask;
    }

    public Task ShowErrorAsync(string title, string message)
    {
        // TODO(Phase 3): replace with Avalonia dialog window.
        Console.Error.WriteLine($"[ERROR] {title}: {message}");
        return Task.CompletedTask;
    }

    public Task<bool> ConfirmAsync(string title, string message)
    {
        // TODO(Phase 3): replace with Avalonia confirm dialog.
        // Default-deny in this stub so accidental wiring cannot destroy data.
        return Task.FromResult(false);
    }
}
