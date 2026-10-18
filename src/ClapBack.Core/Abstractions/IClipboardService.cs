namespace ClapBack.Core.Abstractions;

/// <summary>
/// 把识别结果写进系统剪贴板。
///
/// <para>
/// 这是<b>主要的交付动作</b>，不是自动发送的附属品：用户的流程是「说 → 文字进剪贴板 →
/// 在游戏里自己按 Ctrl+V」。所以识别成功后文字必须留在剪贴板里，不能被随后的还原操作擦掉。
/// </para>
/// </summary>
public interface IClipboardService
{
    /// <summary>把文本写进剪贴板并<b>留在那里</b>，供用户随时粘贴。</summary>
    Task SetTextAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>什么都不做的剪贴板实现，供测试或没有剪贴板能力的宿主使用。</summary>
public sealed class NullClipboardService : IClipboardService
{
    public static readonly NullClipboardService Instance = new();

    public Task SetTextAsync(string text, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
