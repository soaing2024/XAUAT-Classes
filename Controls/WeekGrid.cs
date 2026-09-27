using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using XauatSchedule.Animation;
using XauatSchedule.Core;
using XauatSchedule.Services;

namespace XauatSchedule.Controls;

/// <summary>
/// 课表网格：7 天 × N 节，纯展示。
///
/// 动效设计：
/// · 换周：整层水平位移（弹簧，带一点回弹）+ 单元格 12ms 逐个淡入。
/// · 悬停：弹簧缩放 + 描边加深，从当前值起算，快速划过不抖。
/// · 下一节课：顶部"下一节"标签 + 外圈呼吸光晕，把视线拉过去。
/// · 只动 RenderTransform / Opacity，不触发布局。
/// </summary>
public sealed class WeekGrid : Grid
{
    private readonly Grid _inner = new();
    private readonly TranslateTransform _innerShift = new();
    private readonly ScaleTransform _innerScale = new(1, 1);
    private AnimatedDouble? _shiftX;

    private ScheduleStore _store = null!;
    private int _week = 1;
    private List<SlotRecord> _slots = new();
    private bool _reduceMotion;
    private string? _nextLessonId;

    /// <summary>点某节课：交给上层弹悬浮框（第二参数是被点的块，用来定位）。</summary>
    public event Action<LessonRecord, FrameworkElement>? LessonClicked;

    public WeekGrid()
    {
        _inner.RenderTransform = new TransformGroup { Children = { _innerScale, _innerShift } };
        Children.Add(_inner);
        ClipToBounds = true;
        Background = Brushes.Transparent;
    }

    public void Attach(ScheduleStore store, bool reduceMotion)
    {
        _store = store;
        _reduceMotion = reduceMotion;
        _shiftX = new AnimatedDouble(0, v => _innerShift.X = v, response: _reduceMotion ? 0.6 : 0.34);
    }

    public void SetReducedMotion(bool reduce)
    {
        _reduceMotion = reduce;
        _shiftX = new AnimatedDouble(0, v => _innerShift.X = v, response: reduce ? 0.6 : 0.34);
    }

    /// <summary>渲染某一周；nextLessonId 指定的那节课会高亮（下一节要上的课）。</summary>
    public void Show(int week, int direction = 0, bool animate = true, string? nextLessonId = null)
    {
        var changed = week != _week;
        _week = week;
        _nextLessonId = nextLessonId;
        _slots = _store.Data.Slots.ToList();
        var lessons = _store.ForWeek(week);

        _inner.Children.Clear();
        _inner.RowDefinitions.Clear();
        _inner.ColumnDefinitions.Clear();

        _inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        for (var d = 0; d < 7; d++)
            _inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var _ in _slots)
            _inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        for (var i = 0; i < _slots.Count; i++)
        {
            var t = new TextBlock
            {
                Text = _slots[i].Start,
                FontSize = 9.5,
                FontFamily = (FontFamily)FindResourceSafe("MonoFont")!,
                Foreground = (Brush)FindResourceSafe("TextMutedBrush")!,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 6, 0, 0),
                Opacity = 0.75,
            };
            SetRow(t, i);
            SetColumn(t, 0);
            _inner.Children.Add(t);
        }

        for (var day = 0; day < 7; day++)
        {
            for (var row = 0; row < _slots.Count; row++)
            {
                var line = BuildGridLine(row);
                SetRow(line, row);
                SetColumn(line, day + 1);
                _inner.Children.Add(line);
            }
        }

        foreach (var lesson in lessons)
        {
            var start = Math.Clamp(lesson.StartSlot, 1, Math.Max(1, _slots.Count));
            var span = Math.Clamp(lesson.SlotCount, 1, Math.Max(1, _slots.Count - start + 1));
            var block = BuildLessonBlock(lesson, lesson.Id == _nextLessonId);
            SetRow(block, start - 1);
            SetColumn(block, lesson.Day);
            SetRowSpan(block, span);
            _inner.Children.Add(block);
        }

        PlayWeekTransition(direction, animate && changed);
    }

    private void PlayWeekTransition(int direction, bool animate)
    {
        if (!animate || _reduceMotion || direction == 0)
        {
            _shiftX?.Jump(0);
            AnimateCellsIn(0);
            return;
        }

        // 入场：从偏移处弹簧归位（轻微回弹，让整层"落"下来）
        _innerShift.X = 30 * direction;
        _shiftX = new AnimatedDouble(30 * direction, v => _innerShift.X = v, damping: 0.92, response: 0.32);
        _shiftX.Set(0);
        var fade = new AnimatedDouble(0, v => _inner.Opacity = v, response: 0.2);
        fade.Set(1);
        AnimateCellsIn(12);
    }

    private void AnimateCellsIn(int staggerMs)
    {
        var delay = 0.0;
        foreach (var child in _inner.Children)
        {
            if (child is not FrameworkElement fe || fe is TextBlock) continue;
            fe.Opacity = 0;
            var d = _reduceMotion ? 0 : delay / 1000.0;
            delay += staggerMs / 6.0;
            StartDelayed(d, () =>
            {
                var anim = new AnimatedDouble(0, v => fe.Opacity = v, response: _reduceMotion ? 0.2 : 0.26);
                anim.Set(1);
            });
        }
    }

    private static void StartDelayed(double seconds, Action action)
    {
        if (seconds <= 0.0001) { action(); return; }
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) => { timer.Stop(); action(); };
        timer.Start();
    }

    private Border BuildGridLine(int row)
        => new()
        {
            Background = Brushes.Transparent,
            BorderBrush = (Brush)FindResourceSafe("BorderBrush")!,
            BorderThickness = new Thickness(0, 0, 0, row == _slots.Count - 1 ? 0 : 1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(1, 1, 1, 0),
            IsHitTestVisible = false,
        };

    private Border BuildLessonBlock(LessonRecord lesson, bool isNext)
    {
        var palette = ThemeService.Current;
        var accent = LessonColors.AccentOf(lesson.ColorKey, palette);

        var title = new TextBlock
        {
            Text = lesson.Name,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 34,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(palette.Text),
        };
        var room = new TextBlock
        {
            Text = string.IsNullOrEmpty(lesson.Room) ? "" : lesson.Room,
            FontSize = 10,
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(palette.TextMuted),
        };
        var content = new Grid();
        var stack = new StackPanel { Margin = new Thickness(7, 5, 6, 4) };
        stack.Children.Add(title);
        stack.Children.Add(room);
        content.Children.Add(stack);

        var block = new Border
        {
            Background = new SolidColorBrush(palette.LessonFill(accent)),
            BorderBrush = new SolidColorBrush(palette.LessonBorder(accent)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Margin = new Thickness(1.5),
            Child = content,
            Cursor = Cursors.Hand,
            ToolTip = null,   // 信息改由点击后的悬浮框展示
            Tag = lesson,     // 供诊断/自动化定位到具体课程块
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
        };

        if (isNext)
        {
            // 下一节：外圈呼吸 + 左上角标签，把视线拉过去
            block.BorderBrush = new SolidColorBrush(palette.Hero);
            block.BorderThickness = new Thickness(2);

            var chip = new Border
            {
                Background = new SolidColorBrush(palette.Hero),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(5, 1, 5, 1),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 3, 3, 0),
                Child = new TextBlock
                {
                    Text = "下一节",
                    FontSize = 9,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(palette.OnData),
                },
            };
            content.Children.Add(chip);

            if (!_reduceMotion)
            {
                var breathe = new DoubleAnimation
                {
                    From = 1.0,
                    To = 0.45,
                    Duration = new Duration(TimeSpan.FromMilliseconds(1500)),
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                };
                chip.BeginAnimation(OpacityProperty, breathe);
            }
        }

        var hover = new AnimatedDouble(1, v =>
        {
            if (block.RenderTransform is ScaleTransform st) { st.ScaleX = v; st.ScaleY = v; }
        }, response: 0.28);
        var baseBrush = new SolidColorBrush(palette.LessonBorder(accent));
        var hoverBrush = new SolidColorBrush(isNext ? palette.Hero : Palette.Mix(accent, palette.Text, 0.35));

        block.MouseEnter += (_, _) =>
        {
            hover.Set(1.035);
            block.BorderBrush = hoverBrush;
        };
        block.MouseLeave += (_, _) => hover.Set(1.0);
        block.MouseLeftButtonUp += (_, _) => LessonClicked?.Invoke(lesson, block);
        return block;
    }

    private object? FindResourceSafe(string key) => TryFindResource(key) ?? Application.Current?.TryFindResource(key);
    /// <summary>按课程 id 找到它对应的块（诊断/自动化用）。</summary>
    public FrameworkElement? FindBlock(string lessonId)
        => _inner.Children.OfType<FrameworkElement>()
            .FirstOrDefault(e => e is Border { Tag: LessonRecord l } && l.Id == lessonId);
}
