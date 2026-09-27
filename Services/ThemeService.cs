using System.Windows;
using System.Windows.Media;
using XauatSchedule.Core;

namespace XauatSchedule.Services;

/// <summary>
/// 把 Palette 写进 Application.Resources，XAML 里全部用 DynamicResource 引用，
/// 所以换配色是即时生效的，不需要重建窗口。
/// </summary>
public static class ThemeService
{
    public static Palette Current { get; private set; } = Palette.Get("blue");

    public static event Action<Palette>? ThemeChanged;

    /// <summary>亮/暗两套主题（唯一入口）：亮色 = 青瓷蓝，暗色 = 石墨。</summary>
    public static void ApplyTheme(string? theme)
        => Apply(Palette.Get(string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase) ? "graphite" : "blue"));

    public static void Apply(Palette palette)
    {
        Current = palette;
        var app = Application.Current;
        if (app is null) return;

        var r = app.Resources;
        r["BgBrush"] = Brush(palette.Bg);
        r["SurfaceBrush"] = Brush(Palette.WithAlpha(palette.Surface, palette.IsDark ? 0.72 : 0.86));
        r["SurfaceHoverBrush"] = Brush(Palette.WithAlpha(palette.SurfaceHover, 0.95));
        r["TextBrush"] = Brush(palette.Text);
        r["TextMutedBrush"] = Brush(palette.TextMuted);
        r["BorderBrush"] = Brush(Palette.WithAlpha(palette.Border, palette.IsDark ? 0.75 : 0.9));
        r["DataBrush"] = Brush(palette.Data);
        r["Data2Brush"] = Brush(palette.Data2);
        r["HeroBrush"] = Brush(palette.Hero);
        r["OnDataBrush"] = Brush(palette.OnData);
        // 面板底：比窗口底稍实，保证文字在任何壁纸上都读得清
        r["WindowBgBrush"] = Brush(Palette.WithAlpha(palette.Bg, palette.IsDark ? 0.94 : 0.92));

        ThemeChanged?.Invoke(palette);
    }

    private static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
