using System.IO;
using System.Windows;
using System.Windows.Forms;
using Microsoft.Win32;

namespace XauatSchedule.Native;

/// <summary>
/// 开机自启：写 HKCU\Software\Microsoft\Windows\CurrentVersion\Run，不需要管理员权限，
/// 也会出现在「任务管理器 → 启动应用」里，用户可自行禁用。
/// </summary>
public static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "XAUAT-Classes";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch { return false; }
    }

    public static bool TrySet(bool enabled, out string detail)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) { detail = "无法写入启动项"; return false; }

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) { detail = "无法获取程序路径"; return false; }
                // 自启动时用 --tray 直接静默进入托盘，避免开机弹窗
                key.SetValue(ValueName, $"\"{exe}\" --tray", RegistryValueKind.String);
                detail = exe;
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                detail = "已移除自启动";
            }
            return true;
        }
        catch (Exception ex) { detail = ex.Message; return false; }
    }
}

/// <summary>托盘图标与右键菜单（用 WinForms NotifyIcon，零第三方依赖）。</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Window _window;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _topMostItem;
    private readonly ToolStripMenuItem _autoStartItem;

    public event Action? ExitRequested;
    public event Action? ResetPositionRequested;

    public TrayIcon(Window window, bool topMost, bool autoStart)
    {
        _window = window;

        _toggleItem = new ToolStripMenuItem("显示 / 隐藏");
        _toggleItem.Click += (_, _) => ToggleWindow();

        _topMostItem = new ToolStripMenuItem("置顶") { CheckOnClick = true, Checked = topMost };
        _topMostItem.Click += (_, _) => TopMostToggled?.Invoke(_topMostItem.Checked);

        _autoStartItem = new ToolStripMenuItem("开机自启") { CheckOnClick = true, Checked = autoStart };
        _autoStartItem.Click += (_, _) => AutoStartToggled?.Invoke(_autoStartItem.Checked);

        var resetItem = new ToolStripMenuItem("重置窗口位置");
        resetItem.Click += (_, _) => ResetPositionRequested?.Invoke();

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_topMostItem);
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(resetItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _icon = new NotifyIcon
        {
            Icon = BuildIcon(),
            Text = "XAUAT-Classes",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ToggleWindow();
        };
    }

    public event Action<bool>? TopMostToggled;
    public event Action<bool>? AutoStartToggled;

    public void SyncState(bool topMost, bool autoStart)
    {
        _topMostItem.Checked = topMost;
        _autoStartItem.Checked = autoStart;
    }

    private void ToggleWindow()
    {
        if (_window.IsVisible && _window.WindowState != WindowState.Minimized)
        {
            _window.Hide();
        }
        else
        {
            _window.Show();
            _window.WindowState = WindowState.Normal;
            _window.Activate();
        }
    }

    /// <summary>优先用打包进 exe 的图标（与资源管理器里看到的一致），取不到再现场画一个。</summary>
    private static System.Drawing.Icon BuildIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var ico = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (ico is not null) return ico;
            }
        }
        catch { }
        return DrawIconFallback();
    }

    /// <summary>拿不到 exe 图标时按同一套设计现画（圆角方块 + 课表格）。</summary>
    private static System.Drawing.Icon DrawIconFallback()
    {
        var bmp = new System.Drawing.Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using var bg = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 51, 78, 172));
            using var path = RoundedRect(new System.Drawing.Rectangle(2, 2, 28, 28), 8);
            g.FillPath(bg, path);
            using var ink = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 247, 242, 235));
            g.FillRectangle(ink, 7, 9, 18, 3);
            g.FillRectangle(ink, 7, 14, 8, 9);
            g.FillRectangle(ink, 17, 14, 8, 4);
            g.FillRectangle(ink, 17, 19, 8, 4);
        }
        var handle = bmp.GetHicon();
        return System.Drawing.Icon.FromHandle(handle);
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(System.Drawing.Rectangle r, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void ShowBalloon(string title, string text)
    {
        try { _icon.ShowBalloonTip(2600, title, text, ToolTipIcon.Info); } catch { }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
