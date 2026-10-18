using System.Runtime.InteropServices;
using System.Windows;
using ClapBack.Core.Abstractions;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;
using DataObject = System.Windows.DataObject;
using IDataObject = System.Windows.IDataObject;

namespace ClapBack.Windows.Infrastructure;

/// <summary>
/// 发送前后的剪贴板快照。
/// 不只保存纯文本：<c>Clipboard.SetText</c> 会把用户剪贴板里的图片、文件列表、富文本
/// 全部覆盖掉且无法找回，所以这里按格式逐项快照，恢复时整体写回。
/// </summary>
public sealed record ClipboardSnapshot(string? Text, IReadOnlyDictionary<string, object?> Formats)
{
    public bool HasContent => Text is not null || Formats.Count > 0;
}

public sealed class WindowsClipboardService : IClipboardService
{
    private const int RestoreAttempts = 3;

    public Task<ClipboardSnapshot> CaptureAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return RunOnUiThread(() =>
        {
            var text = Clipboard.ContainsText() ? Clipboard.GetText() : null;
            return new ClipboardSnapshot(text, CaptureFormats());
        });
    }

    public Task SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return RunOnUiThread(() =>
        {
            Clipboard.SetText(text);
            return true;
        });
    }

    /// <summary>读取当前剪贴板文本，用于判断用户是否在发送间隙复制了新内容。</summary>
    public Task<string?> GetTextAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return RunOnUiThread(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);
    }

    /// <summary>
    /// 写回快照。剪贴板被别的进程持有时会短暂锁住，这里做几次退避重试；
    /// 重试仍失败则抛出，由调用方决定是否吞掉（恢复失败绝不能掩盖一次已经成功的发送）。
    /// </summary>
    public async Task RestoreAsync(ClipboardSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await RestoreCoreAsync(snapshot, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (ExternalException) when (attempt + 1 < RestoreAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static Task RestoreCoreAsync(ClipboardSnapshot snapshot, CancellationToken cancellationToken) =>
        RunOnUiThread(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (snapshot.Formats.Count == 0)
            {
                if (snapshot.Text is not null)
                {
                    Clipboard.SetText(snapshot.Text);
                }
                else
                {
                    Clipboard.Clear();
                }

                return true;
            }

            // 多格式恢复：整体一次性 SetDataObject，避免中间状态被别的进程观察到。
            var data = new DataObject();
            foreach (var (format, value) in snapshot.Formats)
            {
                if (value is null)
                {
                    continue;
                }

                try
                {
                    data.SetData(format, value, autoConvert: false);
                }
                catch (ArgumentException)
                {
                    // 某些格式无法重新序列化（例如延迟渲染的自定义格式），跳过该格式而不是放弃整体恢复。
                }
                catch (ExternalException)
                {
                    // 同上。
                }
            }

            Clipboard.SetDataObject(data, copy: true);
            return true;
        });

    private static IReadOnlyDictionary<string, object?> CaptureFormats()
    {
        var captured = new Dictionary<string, object?>(StringComparer.Ordinal);

        IDataObject? data;
        try
        {
            data = Clipboard.GetDataObject();
        }
        catch (ExternalException)
        {
            return captured;
        }

        if (data is null)
        {
            return captured;
        }

        string[] formats;
        try
        {
            formats = data.GetFormats();
        }
        catch (ExternalException)
        {
            return captured;
        }

        foreach (var format in formats)
        {
            if (string.IsNullOrEmpty(format))
            {
                continue;
            }

            try
            {
                captured[format] = data.GetData(format, autoConvert: false);
            }
            catch (ArgumentException)
            {
                // 读不出的格式跳过。
            }
            catch (ExternalException)
            {
                // 读不出的格式跳过。
            }
            catch (NotSupportedException)
            {
                // 读不出的格式跳过。
            }
        }

        return captured;
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
}
