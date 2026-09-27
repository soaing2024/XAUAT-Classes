using System.Windows.Media;

namespace XauatSchedule.Core;

/// <summary>
/// 配色 token。取自 lieflat-charts skill 的 color-presets.js（porcelain / palm / wire）
/// 与 mono-tokens.js 的暗卡块；同一个应用只锁定一套色系。
/// </summary>
public sealed record Palette(
    string Key,
    string DisplayName,
    bool IsDark,
    Color Bg,
    Color Surface,
    Color SurfaceHover,
    Color Text,
    Color TextMuted,
    Color Border,
    Color Data,
    Color Data2,
    Color Hero,
    Color OnData)
{
    public static Color C(string hex)
    {
        var s = hex.TrimStart('#');
        return s.Length switch
        {
            6 => Color.FromRgb(Convert.ToByte(s[0..2], 16), Convert.ToByte(s[2..4], 16), Convert.ToByte(s[4..6], 16)),
            8 => Color.FromArgb(Convert.ToByte(s[0..2], 16), Convert.ToByte(s[2..4], 16),
                                 Convert.ToByte(s[4..6], 16), Convert.ToByte(s[6..8], 16)),
            _ => Colors.Gray,
        };
    }

    public static Color WithAlpha(Color c, double a)
        => Color.FromArgb((byte)Math.Clamp(a * 255, 0, 255), c.R, c.G, c.B);

    /// <summary>按比例把两种颜色混合（0 = a，1 = b）。</summary>
    public static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    /// <summary>课程卡片底色：数据色与背景按明度插值，保证文字可读。</summary>
    public Color LessonFill(Color accent) => IsDark ? Mix(Bg, accent, 0.34) : Mix(Bg, accent, 0.20);
    public Color LessonBorder(Color accent) => IsDark ? Mix(Bg, accent, 0.62) : Mix(Bg, accent, 0.72);

    public static readonly Dictionary<string, Palette> All = new()
    {
        ["blue"] = new Palette(
            "blue", "青瓷蓝", false,
            C("#F7F2EB"), C("#FFFFFF"), C("#F1ECE2"), C("#081F5C"), C("#4A5C86"), C("#DDD6C9"),
            C("#334EAC"), C("#7096D1"), C("#081F5C"), C("#FFFFFF")),

        ["green"] = new Palette(
            "green", "椰林绿", false,
            C("#F0EFEB"), C("#FFFFFF"), C("#E9E8E0"), C("#3A2C1F"), C("#6A5A48"), C("#DAD7CB"),
            C("#43593B"), C("#77835A"), C("#B5860B"), C("#FFFFFF")),

        ["red"] = new Palette(
            "red", "编辑部红", false,
            C("#F0F0EE"), C("#FFFFFF"), C("#E9E9E5"), C("#1F1E1C"), C("#5C5B56"), C("#D8D7D1"),
            C("#22211F"), C("#8F8E86"), C("#E14A25"), C("#FFFFFF")),

        ["graphite"] = new Palette(
            "graphite", "石墨暗色", true,
            C("#15161B"), C("#1D1F26"), C("#252832"), C("#E9EAF0"), C("#9EA3B4"), C("#2E313B"),
            C("#8AA6F0"), C("#5E77C4"), C("#5B7CFA"), C("#0B0C10")),
    };

    public static Palette Get(string key) => All.TryGetValue(key, out var p) ? p : All["blue"];
}

/// <summary>课程色板：抓来的课按名字稳定分配一个色键。</summary>
public static class LessonColors
{
    public static readonly string[] Keys = { "a1", "a2", "a3", "a4", "a5", "a6" };

    private static readonly string[] AccentHex =
    {
        "#334EAC", "#2F7D6B", "#B5860B", "#8A4FB5", "#C2543F", "#3C7DA8",
    };

    public static string KeyOf(string lessonName, Palette palette)
    {
        var idx = Math.Abs(StableHash(lessonName)) % AccentHex.Length;
        return Keys[idx];
    }

    public static Color AccentOf(string colorKey, Palette palette)
    {
        var idx = Array.IndexOf(Keys, colorKey);
        if (idx < 0) return palette.Data;
        var hex = AccentHex[idx];
        var c = Palette.C(hex);
        return palette.IsDark ? Palette.Mix(c, Colors.White, 0.18) : c;
    }

    private static int StableHash(string s)
    {
        unchecked
        {
            var h = 17;
            foreach (var ch in s) h = h * 31 + ch;
            return h;
        }
    }
}
