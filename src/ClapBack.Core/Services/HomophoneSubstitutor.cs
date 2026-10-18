using System.Text;
using ClapBack.Core.Models;

namespace ClapBack.Core.Services;

/// <summary>
/// 敏感词同音替换：把识别出来的脏字换成同音词，让玩家表达原意但不直接骂人。
/// </summary>
/// <remarks>
/// <para>
/// 三条设计约束，都是为了「换得准、不换坏」：
/// </para>
/// <list type="number">
/// <item>
/// <b>最长优先</b>。同一位置能匹配多条规则时先试最长的，否则「妈的」会抢在「他妈的」前面，
/// 把「他妈的」切成两半。
/// </item>
/// <item>
/// <b>单遍扫描，不回看替换结果</b>。匹配到就直接把原文位置跳过去，替换出来的文字<b>不会</b>
/// 再次参与匹配。否则候选词里如果含有另一个敏感词，会被换两次，越换越乱。
/// </item>
/// <item>
/// <b>纯字母词按整词匹配、不区分大小写</b>。<c>sb</c> 要能匹配 <c>SB</c>，但不能匹配
/// <c>usb</c> 或 <c>sbtest</c> 里的那两个字母。中文没有词边界概念，所以这条只对 ASCII 词生效。
/// </item>
/// </list>
/// </remarks>
public sealed class HomophoneSubstitutor
{
    private readonly Rule[] _rules;
    private readonly Random _random;

    private readonly record struct Rule(string Source, string[] Alternatives, bool IsLatinWord);

    /// <param name="entries">替换规则；null 表示没有规则（原样返回）。</param>
    /// <param name="random">随机源，测试里可注入固定种子以得到确定结果。</param>
    public HomophoneSubstitutor(IEnumerable<HomophoneEntry>? entries, Random? random = null)
    {
        _random = random ?? Random.Shared;
        _rules = (entries ?? Array.Empty<HomophoneEntry>())
            .Where(static entry => entry is not null && !string.IsNullOrWhiteSpace(entry.Source))
            .Select(static entry =>
            {
                var source = entry.Source.Trim();
                var alternatives = (entry.Alternatives ?? Array.Empty<string>())
                    .Where(static alternative => !string.IsNullOrWhiteSpace(alternative))
                    .Select(static alternative => alternative.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                return new Rule(source, alternatives, entry.IsLatinWord);
            })
            .Where(static rule => rule.Alternatives.Length > 0)
            // 最长优先，见类注释第 1 条。
            .OrderByDescending(static rule => rule.Source.Length)
            .ToArray();
    }

    /// <summary>是否有可用规则。</summary>
    public bool HasRules => _rules.Length > 0;

    /// <summary>把文本里的敏感词换成同音词。没有命中时原样返回。</summary>
    public string Replace(string? text)
    {
        if (string.IsNullOrEmpty(text) || _rules.Length == 0)
        {
            return text ?? string.Empty;
        }

        var result = new StringBuilder(text.Length + 16);
        var index = 0;
        while (index < text.Length)
        {
            var matched = false;
            foreach (var rule in _rules)
            {
                if (!MatchesAt(text, index, rule))
                {
                    continue;
                }

                result.Append(rule.Alternatives[_random.Next(rule.Alternatives.Length)]);
                // 整段跳过原文，替换结果不回喂 —— 见类注释第 2 条。
                index += rule.Source.Length;
                matched = true;
                break;
            }

            if (!matched)
            {
                result.Append(text[index]);
                index++;
            }
        }

        return result.ToString();
    }

    private static bool MatchesAt(string text, int index, Rule rule)
    {
        var length = rule.Source.Length;
        if (index + length > text.Length)
        {
            return false;
        }

        if (rule.IsLatinWord)
        {
            if (index > 0 && char.IsAsciiLetter(text[index - 1]))
            {
                return false;
            }

            var end = index + length;
            if (end < text.Length && char.IsAsciiLetter(text[end]))
            {
                return false;
            }

            return text.AsSpan(index, length)
                .Equals(rule.Source, StringComparison.OrdinalIgnoreCase);
        }

        return text.AsSpan(index, length).Equals(rule.Source, StringComparison.Ordinal);
    }
}

/// <summary>把字库设置套用到一段识别文本上的便捷入口。</summary>
public static class HomophoneSubstitution
{
    /// <summary>
    /// 按 <paramref name="settings"/> 的开关和字库替换 <paramref name="text"/>。
    /// 关闭开关时原样返回。
    /// </summary>
    public static string Apply(ClapBackSettings settings, string text)
    {
        if (!settings.EnableHomophoneSubstitution)
        {
            return text;
        }

        return new HomophoneSubstitutor(settings.HomophoneEntries).Replace(text);
    }
}
