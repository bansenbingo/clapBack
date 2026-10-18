namespace ClapBack.Core.Services;

/// <summary>
/// 识别结果的收尾整理。
/// </summary>
/// <remarks>
/// 只做一件事：把句末的<b>句号</b>去掉。SenseVoice 这类模型习惯在句尾补一个「。」，
/// 但本程序的交付方式是把文字粘进游戏聊天框（Ctrl+V），多一个句号纯属碍事。
/// 问号、感叹号保留——它们带的是语气，不是标点噪音。
/// </remarks>
public static class TranscriptCleaner
{
    /// <summary>
    /// 去掉结尾的句号类字符（可重复）以及其后的空白。
    /// <para>
    /// 只处理<b>结尾</b>：句中的句号、小数点（<c>3.14</c>）、英文缩写都不受影响。
    /// </para>
    /// </summary>
    public static string StripTrailingPeriod(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var trimmed = text.TrimEnd();
        while (trimmed.Length > 0 && IsTrailingPeriod(trimmed[^1]))
        {
            trimmed = trimmed[..^1].TrimEnd();
        }

        return trimmed;
    }

    private static bool IsTrailingPeriod(char value) => value is '。' or '．' or '.' or '｡';
}
