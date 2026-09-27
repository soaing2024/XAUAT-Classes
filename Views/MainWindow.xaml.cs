using System.Diagnostics;
using System.Text;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using XauatSchedule.Animation;
using XauatSchedule.Controls;
using XauatSchedule.Core;
using XauatSchedule.Native;
using XauatSchedule.Services;

namespace XauatSchedule.Views;

public partial class MainWindow : Window, ICornerAware
{
    private readonly ScheduleStore _store;
    private readonly BackdropMode _mode;
    private readonly DispatcherTimer _saveTimer;
    private int _week;
    private int _lastDirection;
    private double _expandedHeight;
    private XauatClient? _client;
    private bool _gateOpen;
    private bool _panelOpen;
    /// <summary>构造函数里 XAML 初始化会触发若干 ValueChanged（如 Slider 被最小/最大值纠正），
    /// 此时计时器、控件字段还没就绪，所以统一用这个门闩挡住。</summary>
    private BackdropReport _backdrop = new(AppliedBackdrop.Solid, 0, "未初始化");

    public double CornerRadiusValue => _store.Data.Settings.CornerRadius;

    public MainWindow(ScheduleStore store, BackdropMode mode)
    {
        _store = store;
        _mode = mode;

        // 分层窗口必须在建窗之前决定（运行时无法切换），所以模式来自启动参数/设置
        AllowsTransparency = mode == BackdropMode.Layered;

        InitializeComponent();

        // 减少动效不再由设置控制，直接跟随 Windows 的"动画效果"开关
        _store.Data.Settings.ReduceMotion = !SystemParameters.ClientAreaAnimation;

        _week = ResolveInitialWeek();
        _expandedHeight = _store.Data.Settings.WindowHeight;

        SourceInitialized += (_, _) => ApplyWindowChrome();
        Loaded += (_, _) => OnLoaded();
        Closing += (_, e) => { PersistWindowPlacement(); };

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _store.Save(); };

        WireEvents();
        BuildDayHeader();

        KeyDown += OnKeyDown;
    }

    private int ResolveInitialWeek()
    {
        var term = _store.Data.Term;
        var current = WeekMath.CurrentWeek(term);
        var saved = _store.Data.Settings.LastWeek;
        return current ?? Math.Clamp(saved <= 0 ? 1 : saved, 1, term.TotalWeeks);
    }

    // ── 启动 ────────────────────────────────────────────────

    private void OnLoaded()
    {
        RestorePlacement();
        ApplySettingsToUi();
        RefreshAll(animate: false);
        AnimateWindowIn();

        // 本机第一次打开、或已退出登录 → 先盖一层登录页；登录过就直接进展示页
        if (_store.IsFirstRun || !_store.HasLogin) ShowLoginGate();

        // --diag：把布局实情写到临时文件（用于无头验收窗口是否真的画出来了）
        if (Environment.GetCommandLineArgs().Contains("--diag"))
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                try
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"窗口: {ActualWidth:0}x{ActualHeight:0} 透明={AllowsTransparency} 背景={Background}");
                    sb.AppendLine($"Shell: {Shell.ActualWidth:0}x{Shell.ActualHeight:0} 背景={(Shell.Background as SolidColorBrush)?.Color.ToString() ?? "null"} 圆角={Shell.CornerRadius.TopLeft}");
                    sb.AppendLine($"WeekView: {WeekView.ActualWidth:0}x{WeekView.ActualHeight:0} 内容={VisualTreeHelper.GetChildrenCount(WeekView)}");
                    sb.AppendLine($"Chart: {Chart.ActualWidth:0}x{Chart.ActualHeight:0}");
                    sb.AppendLine($"DayHeader: {DayHeader.ActualWidth:0}x{DayHeader.ActualHeight:0} 子项={DayHeader.Children.Count}");
                    sb.AppendLine($"本周标题: {WeekTitle.Text} / {WeekRange.Text} / {NextLine.Text}");
                    sb.AppendLine($"登录层: {(LoginGate.Visibility == Visibility.Visible ? "显示" : "隐藏")} 已登录={_store.HasLogin} 首次运行={_store.IsFirstRun}");
                    sb.AppendLine($"主题: {(ThemeService.Current.IsDark ? "暗色" : "亮色")} 悬浮框: {(CourseTip.Visibility == Visibility.Visible ? "显示" : "隐藏")}");

                    // --tip：模拟点一下"下一节课"，把悬浮框内容写出来供验收
                    if (Environment.GetCommandLineArgs().Contains("--tip"))
                    {
                        var nextLesson = ComputeNextLesson(_week);
                        var anchor = nextLesson is null ? null : WeekView.FindBlock(nextLesson.Id);
                        if (nextLesson is not null && anchor is not null)
                        {
                            ShowCourseTip(nextLesson, anchor);
                            sb.AppendLine($"悬浮框: {CourseTip.Visibility} @({TipShift.X:0},{TipShift.Y:0}) 尺寸 {CourseTip.ActualWidth:0}x{CourseTip.ActualHeight:0}");
                            sb.AppendLine($"  内容: {TipName.Text} | {TipRoom.Text} | {TipTime.Text} | {TipTeacher.Text}");
                        }
                        else sb.AppendLine("悬浮框: 没有找到下一节课的块");
                    }
                    sb.AppendLine($"按钮圆角: {(FindResource("Radius.Button") is CornerRadius cr ? cr.TopLeft : -1)}");
                    sb.AppendLine($"课程块数(第{_week}周): {_store.ForWeek(_week).Count}");
                    sb.AppendLine($"资源检查: BgBrush={(TryFindResource("BgBrush") != null)} WindowBgBrush={(TryFindResource("WindowBgBrush") != null)} TextBrush={(TryFindResource("TextBrush") != null)}");
                    sb.AppendLine($"前景色={Foreground} 字体={(FontFamily?.Source ?? "null")}");
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "xauat-layout.txt"), sb.ToString(), Encoding.UTF8);
                }
                catch (Exception ex) { File.WriteAllText(Path.Combine(Path.GetTempPath(), "xauat-layout.txt"), "诊断失败: " + ex, Encoding.UTF8); }
            };
            t.Start();
        }
    }

    private void AnimateWindowIn()
    {
        if (_store.Data.Settings.ReduceMotion) return;
        Shell.Opacity = 0;
        var fade = new AnimatedDouble(0, v => Shell.Opacity = v, response: 0.24);
        fade.Set(1);
        var scale = new ScaleTransform(0.985, 0.985);
        Shell.RenderTransformOrigin = new Point(0.5, 0.4);
        Shell.RenderTransform = scale;
        var pop = new AnimatedDouble(0.985, v => { scale.ScaleX = v; scale.ScaleY = v; }, response: 0.3);
        pop.Set(1);
    }

    private void ApplyWindowChrome()
    {
        ThemeService.ApplyTheme(_store.Data.Settings.Theme);

        if (_mode != BackdropMode.Layered)
        {
            // 硬件加速路线：普通窗口 + DWM 系统背景，靠 WindowChrome 把客户区做成"玻璃"
            Background = Brushes.Transparent;
            var chrome = new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(6),
                CornerRadius = new CornerRadius(0),
                GlassFrameThickness = new Thickness(-1),
                UseAeroCaptionButtons = false,
            };
            WindowChrome.SetWindowChrome(this, chrome);
        }

        _backdrop = WindowEffects.ApplyBackdrop(this, BackdropMode.Layered, ThemeService.Current);
        try
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "xauat-backdrop.txt"),
                $"请求模式={_mode} 实际生效={_backdrop.DisplayName} 系统build={_backdrop.OsBuild} 说明={_backdrop.Detail}", Encoding.UTF8);
        }
        catch { }
        WindowEffects.HideFromTaskSwitcher(this, hide: true);
        WindowEffects.ApplyZOrder(this, _store.Data.Settings.AlwaysOnTop, _store.Data.Settings.DesktopLevel);
    }

    // ── 布局 ────────────────────────────────────────────────

    private void BuildDayHeader()
    {
        DayHeader.Children.Clear();
        DayHeader.ColumnDefinitions.Clear();
        DayHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        for (var i = 0; i < 7; i++)
            DayHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
    }

    private void UpdateDayHeader()
    {
        DayHeader.Children.Clear();
        for (var day = 1; day <= 7; day++)
        {
            // 日期用“真实日期”（调休周的周日/周六与自然周不一致）
            var date = _store.Data.DateOf(_week, day);
            var isToday = date == DateOnly.FromDateTime(DateTime.Now);
            var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            panel.Children.Add(new TextBlock
            {
                Text = WeekText.DayNamesShort[day],
                FontSize = 11,
                FontWeight = isToday ? FontWeights.Bold : FontWeights.Medium,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = (Brush)FindResource(isToday ? "HeroBrush" : "TextMutedBrush"),
            });
            panel.Children.Add(new TextBlock
            {
                Text = date.ToString("dd"),
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = (Brush)FindResource(isToday ? "HeroBrush" : "TextMutedBrush"),
            });
            System.Windows.Controls.Grid.SetColumn(panel, day);
            DayHeader.Children.Add(panel);
        }
    }

    private void WireEvents()
    {
        Chart.WeekChanged += w => GoToWeek(w, Math.Sign(w - _week));
        WeekView.LessonClicked += (lesson, anchor) => ShowCourseTip(lesson, anchor);
    }

    private void RefreshAll(bool animate = true)
    {
        var term = _store.Data.Term;
        _week = Math.Clamp(_week, 1, term.TotalWeeks);
        _store.Data.Settings.LastWeek = _week;

        var next = ComputeNextLesson(_week);

        WeekView.Attach(_store, _store.Data.Settings.ReduceMotion);
        WeekView.Show(_week, _lastDirection, animate, next?.Id);
        Chart.Attach(_store.WeeklyLoad(), WeekMath.CurrentWeek(term) ?? 0, _week, ThemeService.Current,
            _store.Data.Settings.ReduceMotion);
        Chart.SetWeek(_week, animate);

        var weekLessons = _store.ForWeek(_week);
        WeekTitle.Text = $"第 {_week} 周";
        WeekRange.Text = $"{WeekMath.WeekRange(term, _week)} · {weekLessons.Count} 节 · " +
                         $"{weekLessons.Select(l => l.Day).Distinct().Count()} 天有课";
        NextLine.Text = next is null
            ? "本周没有后续排课"
            : $"下一节 · {DescribeWhen(next)} · {next.Name} · {next.Room}";

        UpdateDayHeader();
        HideCourseTip(immediate: true);
        ScheduleSave();
    }

    /// <summary>算"下一节要上的课"：当前周取最近的未来一节课；未来周取该周第一节课。</summary>
    private LessonRecord? ComputeNextLesson(int week)
    {
        var data = _store.Data;
        var now = DateTime.Now;
        var current = WeekMath.CurrentWeek(data.Term);

        var entries = _store.ForWeek(week)
            .Select(l => (lesson: l, start: StartTimeOf(week, l)))
            .Where(x => x.start != DateTime.MinValue)
            .OrderBy(x => x.start)
            .ToList();
        if (entries.Count == 0) return null;

        if (current is null || week > current) return entries[0].lesson;   // 未来周：第一节
        if (week < current) return null;                                  // 过去的周：不高亮
        var nextEntry = entries.FirstOrDefault(x => x.start >= now);
        return nextEntry.lesson;
    }

    private DateTime StartTimeOf(int week, LessonRecord lesson)
    {
        var slot = _store.Data.Slot(lesson.StartSlot);
        if (slot is null) return DateTime.MinValue;
        var date = _store.Data.DateOf(week, lesson.Day);
        var minutes = WeekMath.ToMinutes(slot.Start);
        return new DateTime(date.Year, date.Month, date.Day, minutes / 60, minutes % 60, 0);
    }

    /// <summary>"周三 10:25（还有 2 天 3 小时）" 这类描述。</summary>
    private string DescribeWhen(LessonRecord lesson)
    {
        var start = StartTimeOf(_week, lesson);
        var day = WeekText.DayNames[lesson.Day];
        var time = _store.Data.TimeRange(lesson.StartSlot, lesson.SlotCount);
        var delta = start - DateTime.Now;
        string rel;
        if (delta.TotalMinutes < -1) rel = "已结束";
        else if (delta.TotalMinutes < 1) rel = "就是现在";
        else if (delta.TotalHours < 1) rel = $"{(int)delta.TotalMinutes} 分钟后";
        else if (delta.TotalDays < 1) rel = $"{(int)delta.TotalHours} 小时后";
        else rel = $"{(int)delta.TotalDays} 天 {delta.Hours} 小时后";
        return $"{day} {time.Split('-')[0].Trim()}（{rel}）";
    }

    // ── 课程悬浮框 ──────────────────────────────────────────
    private FrameworkElement? _tipAnchor;
    private DispatcherTimer? _tipTimer;

    private void ShowCourseTip(LessonRecord lesson, FrameworkElement anchor)
    {
        _tipAnchor = anchor;
        TipName.Text = lesson.Name;
        TipRoom.Text = "📍 " + (string.IsNullOrEmpty(lesson.Room) ? "教室待定" : lesson.Room);
        TipTime.Text = "🕘 " + DescribeWhen(lesson) + " · " + WeekText.FormatWeeks(lesson.Weeks);
        TipTeacher.Text = "👤 " + (string.IsNullOrEmpty(lesson.Teacher) ? "教师待定" : lesson.Teacher);

        CourseTip.Visibility = Visibility.Visible;
        CourseTip.UpdateLayout();

        // 贴着被点的课程块放，超出窗口就往回收
        var origin = anchor.TransformToAncestor(RootGrid).Transform(new Point(0, 0));
        var w = CourseTip.ActualWidth > 0 ? CourseTip.ActualWidth : 240;
        var h = CourseTip.ActualHeight > 0 ? CourseTip.ActualHeight : 90;
        var left = Math.Clamp(origin.X - 6, 8, Math.Max(8, RootGrid.ActualWidth - w - 8));
        var top = origin.Y + anchor.ActualHeight + 6;
        if (top + h > RootGrid.ActualHeight - 8) top = Math.Max(8, origin.Y - h - 6);
        TipShift.X = left;
        TipShift.Y = top;

        if (_store.Data.Settings.ReduceMotion) { TipScale.ScaleX = TipScale.ScaleY = 1; CourseTip.Opacity = 1; }
        else
        {
            CourseTip.Opacity = 0;
            TipScale.ScaleX = TipScale.ScaleY = 0.96;
            var fade = new AnimatedDouble(0, v => CourseTip.Opacity = v, response: 0.22);
            fade.Set(1);
            var pop = new AnimatedDouble(0.96, v => { TipScale.ScaleX = v; TipScale.ScaleY = v; }, damping: 0.9, response: 0.28);
            pop.Set(1);
        }

        // 鼠标离开课程块 / 悬浮框后自然淡出
        anchor.MouseLeave -= OnTipAnchorLeave;
        anchor.MouseLeave += OnTipAnchorLeave;
        CourseTip.MouseLeave -= OnTipLeave;
        CourseTip.MouseLeave += OnTipLeave;
    }

    private void OnTipAnchorLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (CourseTip.IsMouseOver) return;
        ScheduleTipHide();
    }

    private void OnTipLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_tipAnchor?.IsMouseOver == true) return;
        ScheduleTipHide();
    }

    private void ScheduleTipHide()
    {
        _tipTimer?.Stop();
        _tipTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _tipTimer.Tick += (_, _) =>
        {
            _tipTimer?.Stop();
            if (CourseTip.IsMouseOver || _tipAnchor?.IsMouseOver == true) return;
            HideCourseTip();
        };
        _tipTimer.Start();
    }

    private void HideCourseTip(bool immediate = false)
    {
        if (CourseTip.Visibility != Visibility.Visible) return;
        _tipTimer?.Stop();
        if (immediate || _store.Data.Settings.ReduceMotion)
        {
            CourseTip.Visibility = Visibility.Collapsed;
            CourseTip.Opacity = 0;
            return;
        }
        var fade = new AnimatedDouble(CourseTip.Opacity, v => CourseTip.Opacity = v, response: 0.2);
        fade.Set(0);
        var shrink = new AnimatedDouble(TipScale.ScaleX, v => { TipScale.ScaleX = v; TipScale.ScaleY = v; }, response: 0.22);
        shrink.Set(0.97);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        timer.Tick += (_, _) => { timer.Stop(); if (CourseTip.Opacity < 0.05) CourseTip.Visibility = Visibility.Collapsed; };
        timer.Start();
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string theme) return;
        _store.Data.Settings.Theme = theme;
        ThemeService.ApplyTheme(theme);
        SyncThemeButtons();
        Chart.SetPalette(ThemeService.Current);
        RefreshAll(animate: false);
        _store.Save();
    }

    private void SyncThemeButtons()
    {
        var dark = ThemeService.Current.IsDark;
        BtnThemeLight.Style = (Style)FindResource(dark ? "PillButton" : "PrimaryButton");
        BtnThemeDark.Style = (Style)FindResource(dark ? "PrimaryButton" : "PillButton");
    }

    private void GoToWeek(int week, int direction)
    {
        var target = Math.Clamp(week, 1, _store.Data.Term.TotalWeeks);
        if (target == _week) return;
        _lastDirection = direction == 0 ? Math.Sign(target - _week) : direction;
        _week = target;
        RefreshAll();
    }

    private void ScheduleSave()
    {
        if (_saveTimer is null) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    // ── 选中与详情 ──────────────────────────────────────────



    // ── 面板 ────────────────────────────────────────────────







    // ── 面板动画 ────────────────────────────────────────────

    private void ShowPanel(UIElement pane)
    {
        // 只剩两个面板：数据来源（抓取）与外观设置；课程本身只能看，不能改
        foreach (var p in new UIElement[] { PaneSettings })
            p.Visibility = ReferenceEquals(p, pane) ? Visibility.Visible : Visibility.Collapsed;

        if (_panelOpen) return;
        _panelOpen = true;
        OverlayHost.Visibility = Visibility.Visible;   // 不做高度动画，避免触发布局

        if (_store.Data.Settings.ReduceMotion)
        {
            PanelShift.X = 0;
            Scrim.Opacity = 0.18;
            return;
        }

        PanelShift.X = PanelHost.Width;
        var slide = new AnimatedDouble(PanelHost.Width, v => PanelShift.X = v, response: 0.3);
        slide.Set(0);
        var scrim = new AnimatedDouble(0, v => Scrim.Opacity = v, response: 0.28);
        scrim.Set(0.18);
    }

    private void ClosePanel()
    {
        if (!_panelOpen) return;
        _panelOpen = false;

        if (_store.Data.Settings.ReduceMotion)
        {
            OverlayHost.Visibility = Visibility.Collapsed;
            return;
        }

        var slide = new AnimatedDouble(PanelShift.X, v => PanelShift.X = v, response: 0.22);
        slide.Set(PanelHost.Width + 20);
        var scrim = new AnimatedDouble(Scrim.Opacity, v => Scrim.Opacity = v, response: 0.2);
        scrim.Set(0);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(240) };
        timer.Tick += (_, _) => { timer.Stop(); if (!_panelOpen) OverlayHost.Visibility = Visibility.Collapsed; };
        timer.Start();
    }

    // ── 设置 ────────────────────────────────────────────────



    private void ApplySettingsToUi()
    {
        Shell.CornerRadius = new CornerRadius(18);
        PanelHost.CornerRadius = new CornerRadius(16);
        SyncThemeButtons();
        BackdropInfoSafe();
        var term = _store.Data.Term;
        var who = string.IsNullOrEmpty(term.StudentName) ? "" : $" · {term.StudentName}";
        var klass = string.IsNullOrEmpty(term.AdminClass) ? "" : $" · {term.AdminClass}";
        AboutText.Text = $"XAUAT-Classes v1.1（只读展示）· {term.Name}{who}{klass} · " +
                         $"{_store.Data.Lessons.Count} 条排课 · 更新于 {_store.Data.FetchedAt ?? "尚未抓取"}";
    }

    private void BackdropInfoSafe() { }

    // ── 窗口行为 ────────────────────────────────────────────

    private void RestorePlacement()
    {
        var s = _store.Data.Settings;
        Width = Math.Max(MinWidth, s.WindowWidth);
        Height = Math.Max(MinHeight, s.WindowHeight);
        _expandedHeight = Height;

        var wa = SystemParameters.WorkArea;
        if (s.WindowX is double x && s.WindowY is double y && x > wa.Left - 40 && x < wa.Right - 120
            && y > wa.Top - 10 && y < wa.Bottom - 80)
        {
            Left = x; Top = y;
        }
        else
        {
            // 默认在屏幕中心启动
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Top + (wa.Height - Height) / 2;
        }
    }

    public void PersistWindowPlacement()
    {
        var s = _store.Data.Settings;
        s.WindowX = Left; s.WindowY = Top;
        s.WindowWidth = Width;
        s.WindowHeight = RestoreBounds.Height > 0 ? RestoreBounds.Height : Height;
        s.LastWeek = _week;
        _store.Save();
    }

    public void ResetPosition()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Top + (wa.Height - Height) / 2;
        if (_store.Data.Settings.CompactView) ToggleCompact(false);
        PersistWindowPlacement();
    }

    public void ApplyTopMost(bool on, bool notify)
    {
        _store.Data.Settings.AlwaysOnTop = on;
        WindowEffects.ApplyZOrder(this, on, _store.Data.Settings.DesktopLevel);
        BtnPin.Opacity = on ? 1 : 0.4;
        App.Current.SyncTray(on, AutoStart.IsEnabled());
        _store.Save();
        if (notify) App.Current.Notify("窗口置顶", on ? "已置顶" : "已取消置顶");
    }

    public void SyncSettingsUi() => ApplySettingsToUi();

    private void ToggleCompact(bool? force = null)
    {
        var target = force ?? !_store.Data.Settings.CompactView;
        _store.Data.Settings.CompactView = target;
        if (target)
        {
            _expandedHeight = Height;
            Height = 150;
        }
        else
        {
            Height = _expandedHeight <= 200 ? 748 : _expandedHeight;
        }
        ScheduleSave();
    }

    // ── 事件 ────────────────────────────────────────────────

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { ToggleCompact(); return; }
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left: GoToWeek(_week - 1, -1); break;
            case Key.Right: GoToWeek(_week + 1, 1); break;
            case Key.Home: GoToWeek(WeekMath.CurrentWeek(_store.Data.Term) ?? _week, 0); break;
            case Key.Escape:
                    if (_panelOpen) ClosePanel(); else HideCourseTip();
                    break;
        }
    }

    private void Today_Click(object sender, RoutedEventArgs e)
        => GoToWeek(WeekMath.CurrentWeek(_store.Data.Term) ?? _week, 0);

    private void Pin_Click(object sender, RoutedEventArgs e) => ApplyTopMost(!_store.Data.Settings.AlwaysOnTop, false);

    private void Minimize_Click(object sender, RoutedEventArgs e) => Hide();

    private void Close_Click(object sender, RoutedEventArgs e) => App.Current.ExitApp();

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        ApplySettingsToUi();
        ShowPanel(PaneSettings);
    }


    private void Panel_Close(object sender, RoutedEventArgs e) => ClosePanel();

    private void Scrim_Click(object sender, MouseButtonEventArgs e) => ClosePanel();









    // ── 首次登录 / 刷新 / 退出登录 ─────────────────────────

    /// <summary>本机第一次打开、或还没有登录态时，盖一层登录页。</summary>
    private void ShowLoginGate(bool animate = true)
    {
        if (_gateOpen) return;
        _gateOpen = true;

        if (_store.TryGetCredentials(out var savedUser, out _))
            GateUser.Text = savedUser;
        else if (!string.IsNullOrEmpty(_store.Data.Settings.LastStudentId))
            GateUser.Text = _store.Data.Settings.LastStudentId!;
        GatePass.Password = "";
        GateCaptcha.Text = "";
        GateCaptchaBox.Visibility = Visibility.Collapsed;
        GateProgressBox.Visibility = Visibility.Collapsed;
        GateStatus.Text = _store.IsFirstRun ? "输入学号与密码后会自动抓取本学期课表。" : "登录已过期，请重新输入密码。";

        LoginGate.Visibility = Visibility.Visible;
        if (!animate || _store.Data.Settings.ReduceMotion)
        {
            GateScrim.Opacity = 0.98;
            return;
        }
        GateScrim.Opacity = 0;
        var fade = new AnimatedDouble(0, v => GateScrim.Opacity = v, response: 0.24);
        fade.Set(0.98);
        GateCard.Opacity = 0;
        var card = new AnimatedDouble(0, v => GateCard.Opacity = v, response: 0.28);
        card.Set(1);
        var scale = new ScaleTransform(0.98, 0.98);
        GateCard.RenderTransformOrigin = new Point(0.5, 0.5);
        GateCard.RenderTransform = scale;
        var pop = new AnimatedDouble(0.98, v => { scale.ScaleX = v; scale.ScaleY = v; }, response: 0.3);
        pop.Set(1);
    }

    private void HideLoginGate()
    {
        _gateOpen = false;
        if (_store.Data.Settings.ReduceMotion)
        {
            LoginGate.Visibility = Visibility.Collapsed;
            return;
        }
        var fade = new AnimatedDouble(GateScrim.Opacity, v => GateScrim.Opacity = v, response: 0.2);
        fade.Set(0);
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        t.Tick += (_, _) => { t.Stop(); if (!_gateOpen) LoginGate.Visibility = Visibility.Collapsed; };
        t.Start();
    }



    private void Gate_OpenBrowser(object sender, RoutedEventArgs e)
    {
        var url = GateChallengeBox.Tag as string ?? "https://swjw.xauat.edu.cn/student/login";
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    private async void Gate_Retry(object sender, RoutedEventArgs e) => await SubmitLoginAsync();

    private async void Gate_Submit(object sender, RoutedEventArgs e) => await SubmitLoginAsync();

    private async Task SubmitLoginAsync()
    {
        var user = GateUser.Text.Trim();
        var pass = GatePass.Password;
        if (user.Length == 0 || pass.Length == 0)
        {
            GateStatus.Text = "学号与密码都要填。";
            return;
        }

        GateStatus.Text = "正在连接统一身份认证…";
        GateProgressBox.Visibility = Visibility.Visible;
        SetProgress(GateProgress, GateProgressBox, 0.06);
        GatePass.IsEnabled = false;

        var ok = await RunFetchAsync(user, pass, GateCaptcha.Text.Trim(), GateProgress, GateProgressBox, GateStatus);

        GatePass.IsEnabled = true;
        if (ok)
        {
            _store.SaveCredentials(user, pass);   // DPAPI 加密后写入，磁盘上无明文
            HideLoginGate();
            RefreshAll(animate: false);
            App.Current.Notify("课表已就绪", "已抓取本学期课表");
        }
    }

    /// <summary>设置里的「刷新课表」：用已保存的凭据一键重抓。</summary>
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (!_store.TryGetCredentials(out var user, out var pass))
        {
            GateStatus.Text = "需要重新输入密码才能刷新。";
            ShowLoginGate();
            return;
        }

        BtnRefresh.IsEnabled = false;
        RefreshProgressBox.Visibility = Visibility.Visible;
        RefreshStatus.Text = "正在刷新…";
        SetProgress(RefreshProgress, RefreshProgressBox, 0.08);

        var ok = await RunFetchAsync(user, pass, "", RefreshProgress, RefreshProgressBox, RefreshStatus);

        BtnRefresh.IsEnabled = true;
        if (!ok)
        {
            // 需要验证码一类的情况，交给登录页处理
            ShowLoginGate();
            GateStatus.Text = RefreshStatus.Text;
        }
        else
        {
            RefreshAll(animate: false);
        }
    }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        _store.ClearCredentials();
        RefreshStatus.Text = "已退出登录：本机保存的密码已清除。";
        GateStatus.Text = "已退出登录。重新输入密码即可继续使用。";
        ShowLoginGate();
    }

    /// <summary>
    /// 统一的抓取流程：登录 → 逐周抓取 → 合并进课表。
    /// 需要验证码时把输入框显示出来并返回 false，由调用方让用户补填。
    /// </summary>
    private async Task<bool> RunFetchAsync(string user, string pass, string captcha,
        Border bar, Border box, TextBlock status)
    {
        try
        {
            _client ??= new XauatClient();
            var login = await _client.LoginAsync(user, pass, captcha.Length > 0 ? captcha : null);

            if (login.NeedsCaptcha)
            {
                GateCaptchaBox.Visibility = Visibility.Visible;
                if (login.CaptchaImage is { Length: > 0 })
                    GateCaptchaImage.Source = BytesToImage(login.CaptchaImage);
                status.Text = login.Message + "，填好验证码后再点一次。";
                box.Visibility = Visibility.Collapsed;
                if (_panelOpen) ShowLoginGate(false);
                return false;
            }

            if (!login.Success)
            {
                status.Text = "登录失败：" + login.Message;
                box.Visibility = Visibility.Collapsed;
                if (login.NeedsManual) ShowChallenge(login.ManualDetail, login.ManualUrl);
                return false;
            }

            status.Text = "正在读取课程列表…";
            SetProgress(bar, box, 0.12);
            var boot = await _client.BootstrapAsync(_store.Data.Settings.LastSemesterId);
            if (!boot.Success)
            {
                status.Text = "读取课程失败：" + boot.Message;
                box.Visibility = Visibility.Collapsed;
                return false;
            }

            _store.ApplyBootstrap(boot, user);
            status.Text = $"已找到 {boot.LessonIds!.Length} 门次，开始抓取 {boot.WeekCount} 周课表…";

            var result = await _client.FetchAllAsync(_store.Data.Term, boot.SemesterId, boot.StdPersonId,
                boot.LessonIds!, boot.Slots!,
                new Progress<(int week, int total, string message)>(p =>
                {
                    SetProgress(bar, box, 0.12 + 0.86 * p.week / Math.Max(1, p.total));
                    status.Text = p.message;
                }));

            if (!result.Success)
            {
                status.Text = "抓取失败：" + result.Message;
                if (result.NeedsManual) ShowChallenge(result.ManualDetail, result.ManualUrl);
                box.Visibility = Visibility.Collapsed;
                return false;
            }

            SetProgress(bar, box, 1);
            _store.MergeFetched(result.Lessons, result.FetchedAt);
            _store.Data.FetchedAt = result.FetchedAt;
            status.Text = $"抓取完成：{result.Message}（{result.FetchedAt}）";
            return true;
        }
        catch (Exception ex)
        {
            status.Text = "出错了：" + ex.Message;
            box.Visibility = Visibility.Collapsed;
            return false;
        }
    }


    /// <summary>反爬 / 人机校验：把服务器原文展示给用户，并给浏览器入口。</summary>
    private void ShowChallenge(string detail, string? url)
    {
        GateChallengeText.Text = detail;
        GateChallengeBox.Tag = url ?? "https://swjw.xauat.edu.cn/student/login";
        GateChallengeBox.Visibility = Visibility.Visible;
        if (!_gateOpen) ShowLoginGate(false);
    }

    private void SetProgress(Border bar, Border box, double ratio)
    {
        var width = box.ActualWidth > 0 ? box.ActualWidth : 300;
        var target = Math.Max(6, width * Math.Clamp(ratio, 0, 1));
        if (_store.Data.Settings.ReduceMotion) { bar.Width = target; return; }
        var anim = new AnimatedDouble(bar.Width, v => bar.Width = v, response: 0.26);
        anim.Set(target);
    }

    private static BitmapImage BytesToImage(byte[] bytes)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.StreamSource = new MemoryStream(bytes);
        img.EndInit();
        img.Freeze();
        return img;
    }
}
