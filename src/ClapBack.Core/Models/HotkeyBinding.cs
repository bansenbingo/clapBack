namespace ClapBack.Core.Models;

/// <summary>ClapBack 支持的全局热键；解析结果直接用于 Windows RegisterHotKey。</summary>
public sealed record HotkeyBinding(string Name, ushort VirtualKey, uint Modifiers)
{
    public static bool TryParse(string? value, out HotkeyBinding binding)
    {
        binding = new("CapsLock", 0x14, 0);
        var parts = (value ?? string.Empty).Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Any(string.IsNullOrWhiteSpace)) return false;
        uint modifiers = 0;
        var names = new List<string>();
        foreach (var part in parts[..^1])
        {
            var (flag, name) = part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => (2u, "Ctrl"),
                "ALT" => (1u, "Alt"),
                "SHIFT" => (4u, "Shift"),
                "WIN" or "WINDOWS" => (8u, "Win"),
                _ => (0u, string.Empty)
            };
            if (flag == 0 || (modifiers & flag) != 0) return false;
            modifiers |= flag;
            names.Add(name);
        }

        var key = parts[^1].ToUpperInvariant();
        ushort virtualKey;
        string keyName;
        if (key == "CAPSLOCK") { virtualKey = 0x14; keyName = "CapsLock"; }
        else if (key == "ENTER") { virtualKey = 0x0D; keyName = "Enter"; }
        else if (key == "SPACE") { virtualKey = 0x20; keyName = "Space"; }
        else if (key.StartsWith('F') && ushort.TryParse(key[1..], out var number) && number is >= 1 and <= 24)
        { virtualKey = (ushort)(0x70 + number - 1); keyName = $"F{number}"; }
        else if (modifiers != 0 && key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]))
        { virtualKey = key[0]; keyName = key; }
        else return false;
        names.Add(keyName);
        binding = new(string.Join('+', names), virtualKey, modifiers);
        return true;
    }
}
