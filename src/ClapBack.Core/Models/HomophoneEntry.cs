using System.Text.Json.Serialization;

namespace ClapBack.Core.Models;

/// <summary>
/// 一条敏感词同音替换规则：识别到 <see cref="Source"/> 时，换成
/// <see cref="Alternatives"/> 里随机挑出的一个同音词。
/// </summary>
/// <remarks>
/// 替换候选必须是<b>同音或近音词</b>，且本身不含脏字——目的是「表达原意但不骂人」，
/// 不是把脏话换成另一种脏话。
/// </remarks>
public sealed record HomophoneEntry(string Source, IReadOnlyList<string> Alternatives)
{
    /// <summary>是否是纯 ASCII 字母词（如 <c>sb</c>）。这类词按整词匹配，避免嵌在别的单词里被误伤。</summary>
    [JsonIgnore]
    public bool IsLatinWord =>
        Source.Length > 0 && Source.All(char.IsAsciiLetter);
}

/// <summary>内置的敏感词同音替换字库。</summary>
/// <remarks>
/// <para>
/// 用户可以在 GUI 的「字库」里增删改；这里只提供一份开箱即用的默认值。
/// </para>
/// <para>
/// <b>默认只收录两字及以上的词。</b>单字脏话（如「操」）如果按子串替换，
/// 会把「操作」「操场」这类正常词一起改坏；中文没有空格，没法可靠地做整词判定。
/// 确实需要单字规则时可以自己加，但要接受它可能误伤别的词。
/// </para>
/// </remarks>
public static class HomophoneDefaults
{
    public static IReadOnlyList<HomophoneEntry> All { get; } = Array.AsReadOnly(new[]
    {
        Entry("傻逼", "傻必", "啥必", "沙币", "煞比", "啥波一", "沙波一", "艾斯比", "埃斯比"),
        Entry("傻B", "傻必", "沙币", "煞比", "啥波一"),
        Entry("二逼", "二必", "二比", "艾尔比"),
        Entry("装逼", "装必", "装比"),
        Entry("妈逼", "嘛壁", "麻比"),
        Entry("他妈的", "踏马的", "塔马的", "特么的"),
        Entry("妈的", "嘛的", "麻的", "玛的"),
        Entry("操你妈", "草泥马", "擦泥马"),
        Entry("鸡巴", "鸡吧", "机巴", "吉拔"),
        Entry("白痴", "白吃", "柏痴", "白魑"),
        Entry("脑残", "脑蚕", "瑙残", "脑惭"),
        Entry("弱智", "若智", "箬智", "若志"),
        Entry("神经病", "神精病", "神金病"),
        Entry("废物", "费物", "沸物", "肺物"),
        Entry("贱人", "剑人", "鉴人", "舰人"),
        Entry("婊子", "表子", "彪子"),
        Entry("狗屎", "苟史", "枸史", "苟始"),
        Entry("混蛋", "浑蛋", "昏蛋", "荤蛋"),
        Entry("滚蛋", "滚旦", "鲧蛋"),
        Entry("王八", "王霸", "旺八"),
        Entry("蠢货", "蠢获", "淳货"),
        Entry("有病", "有丙", "友病"),
        Entry("去死", "去斯", "趣死"),
        Entry("sb", "艾斯比", "埃斯比"),
    });

    private static HomophoneEntry Entry(string source, params string[] alternatives) =>
        new(source, Array.AsReadOnly(alternatives));
}
