using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using XauatSchedule.Core;

namespace XauatSchedule.Native;

public enum AppliedBackdrop
{
    /// <summary>分层窗口 + 像素级 alpha（真半透明，软件渲染）。</summary>
    LayeredAlpha,
    /// <summary>DWM 系统背景（Mica / Acrylic），硬件加速。</summary>
    DwmBackdrop,
    /// <summary>系统模糊（未公开接口），实测在 Win11 会把窗口做得几乎不透明，仅作诊断用。</summary>
    AccentBlur,
    /// <summary>兜底纯色。</summary>
    Solid,
}

public sealed record BackdropReport(AppliedBackdrop Applied, int OsBuild, string Detail)
{
    public string DisplayName => Applied switch
    {
        AppliedBackdrop.LayeredAlpha => "真半透明（分层 alpha）",
        AppliedBackdrop.DwmBackdrop => "系统背景（硬件加速）",
        AppliedBackdrop.AccentBlur => "系统模糊（诊断）",
        _ => "纯色兜底",
    };
}

/// <summary>
/// 窗口外观的 Win32 层：圆角、系统背景、暗色标题栏、置顶/贴桌面、点击穿透。
/// 所有 P/Invoke 都收口在这里，UI 层不做互操作。
/// </summary>
public static class WindowEffects
{
    // ── DWM ────────────────────────────────────────────────
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;

    private const int DwmsbtAuto = 0;
    private const int DwmsbtNone = 1;
    private const int DwmsbtMainWindow = 2;      // Mica
    private const int DwmsbtTransientWindow = 3; // Acrylic
    private const int DwmsbtTabbedWindow = 4;    // Mica Alt

    private const int DwmwcpDefault = 0;
    private const int DwmwcpDoNotRound = 1;
    private const int DwmwcpRound = 2;
    private const int DwmwcpRoundSmall = 3;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    // ── User32 ─────────────────────────────────────────────
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    private static readonly IntPtr HwndTopMost = new(-1);
    private static readonly IntPtr HwndNotTopMost = new(-2);
    private static readonly IntPtr HwndBottom = new(1);

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    // ── 公开入口 ───────────────────────────────────────────

    /// <summary>
    /// 应用背景模式。注意：分层模式要求 Window.AllowsTransparency 在建窗前就设为 true，
    /// 所以运行期切换模式需要重启窗口（设置面板会提示）。
    /// </summary>
    public static BackdropReport ApplyBackdrop(Window window, BackdropMode mode, Palette palette)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var build = Environment.OSVersion.Version.Build;
        if (hwnd == IntPtr.Zero) return new BackdropReport(AppliedBackdrop.Solid, build, "窗口句柄为空");

        var dark = palette.IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        ApplyCorners(hwnd, window);

        // 用户显式选择真半透明
        if (mode == BackdropMode.Layered && window.AllowsTransparency)
            return new BackdropReport(AppliedBackdrop.LayeredAlpha, build, "分层窗口像素级 alpha");

        // 丝滑优先 / 自动：优先 DWM 系统背景（硬件加速的真模糊）
        if (build >= 22000)
        {
            var backdrop = palette.IsDark ? DwmsbtTransientWindow : DwmsbtMainWindow;
            var hr = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
            DwmGetWindowAttribute(hwnd, DwmwaSystemBackdropType, out var read, sizeof(int));
            if (hr == 0 && read == backdrop)
                return new BackdropReport(AppliedBackdrop.DwmBackdrop, build,
                    backdrop == DwmsbtMainWindow ? "Mica（硬件加速）" : "Acrylic（硬件加速）");
        }

        if (window.AllowsTransparency)
            return new BackdropReport(AppliedBackdrop.LayeredAlpha, build, "DWM 不支持系统背景，回退分层 alpha");

        var none = DwmsbtNone;
        DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref none, sizeof(int));
        return new BackdropReport(AppliedBackdrop.Solid, build, $"build {build} 不支持系统背景");
    }

    private static void ApplyCorners(IntPtr hwnd, Window window)
    {
        var radius = window is ICornerAware aware ? aware.CornerRadiusValue : 18;
        // 大圆角交给 DWM（贴边更自然），小圆角用 WPF 自己画
        var pref = radius >= 16 ? DwmwcpRound : radius >= 6 ? DwmwcpRoundSmall : DwmwcpDoNotRound;
        try { DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref pref, sizeof(int)); } catch { }
    }

    /// <summary>窗口是否按置顶/贴桌面放置。</summary>
    public static void ApplyZOrder(Window window, bool topMost, bool desktopLevel)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        if (topMost)
        {
            SetWindowPos(hwnd, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }
        else if (desktopLevel)
        {
            // 贴桌面：压到所有普通窗口之下（不抢桌面图标焦点）
            SetWindowPos(hwnd, HwndBottom, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }
        else
        {
            SetWindowPos(hwnd, HwndNotTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }
    }

    /// <summary>隐藏任务栏与 Alt+Tab 条目（小组件常驻时需要）。</summary>
    public static void HideFromTaskSwitcher(Window window, bool hide)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var style = GetWindowLong(hwnd, GwlExStyle);
        style = hide ? style | WsExToolWindow : style & ~WsExToolWindow;
        SetWindowLong(hwnd, GwlExStyle, style);
    }

    /// <summary>整窗点击穿透（贴桌面模式下可选）。</summary>
    public static void SetClickThrough(Window window, bool enabled)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var style = GetWindowLong(hwnd, GwlExStyle);
        style = enabled ? style | WsExTransparent | WsExNoActivate : style & ~(WsExTransparent | WsExNoActivate);
        SetWindowLong(hwnd, GwlExStyle, style);
    }

    public static bool IsForeground(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        return hwnd != IntPtr.Zero && GetForegroundWindow() == hwnd;
    }
}

/// <summary>窗口实现它来告诉 Native 层当前圆角半径。</summary>
public interface ICornerAware
{
    double CornerRadiusValue { get; }
}
