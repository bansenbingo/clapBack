using System.Windows;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;

namespace ClapBack.Windows.Infrastructure;

public sealed record ClipboardSnapshot(string? Text);

public sealed class WindowsClipboardService
{
    public Task<ClipboardSnapshot> CaptureAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return RunOnUiThread(() => new ClipboardSnapshot(Clipboard.ContainsText() ? Clipboard.GetText() : null));
    }

    public Task SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return RunOnUiThread(() => Clipboard.SetText(text));
    }

    public Task RestoreAsync(ClipboardSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot.Text is null)
        {
            return RunOnUiThread(() => Clipboard.Clear());
        }
        else
        {
            return RunOnUiThread(() => Clipboard.SetText(snapshot.Text));
        }
    }

    private static Task<T> RunOnUiThread<T>(Func<T> action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            return Task.FromResult(action());
        }

        return dispatcher.InvokeAsync(action).Task;
    }

    private static Task RunOnUiThread(Action action) => RunOnUiThread(() =>
    {
        action();
        return true;
    });
}
