using System.Diagnostics;

namespace ClapBack.Core.Services;

/// <summary>
/// 本机进程探测：给「自动发送的目标进程」这类设置项提供可选项，避免用户手打进程名打错。
/// </summary>
/// <remarks>
/// 只读取进程名（不含 <c>.exe</c>），不碰窗口内容、不记录标题——目标进程白名单只要名字。
/// 本机探测失败（权限、平台不支持）时返回空列表，界面仍保留手填能力，绝不会因为枚举失败卡住设置窗口。
/// </remarks>
public static class LocalProcessCatalog
{
    /// <summary>有可见主窗口的进程名（去重、按字母序）。这类进程才可能是「前台游戏」。</summary>
    public static IReadOnlyList<string> ListWindowProcessNames()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<string>();
        }

        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in SafeGetProcesses())
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(process.MainWindowTitle))
                {
                    names.Add(process.ProcessName);
                }
            }
            catch
            {
                // 单个进程读不到就跳过：进程可能在枚举中途退出。
            }
            finally
            {
                process.Dispose();
            }
        }

        return names.ToArray();
    }

    /// <summary>
    /// 规范化「逗号分隔的进程白名单」：按中英文逗号/分号/空白切分，去掉 <c>.exe</c> 后缀、
    /// 去空、按出现顺序去重，再用英文逗号拼回去。界面上「添加所选进程」也走这里。
    /// </summary>
    public static string NormalizeList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var token in value.Split([',', '，', ';', '；', ' ', '\t', '\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = token.Trim();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^4];
            }

            if (name.Length == 0 || !seen.Add(name))
            {
                continue;
            }

            result.Add(name);
        }

        return string.Join(",", result);
    }

    private static IEnumerable<Process> SafeGetProcesses()
    {
        try
        {
            return Process.GetProcesses();
        }
        catch
        {
            return Array.Empty<Process>();
        }
    }
}
