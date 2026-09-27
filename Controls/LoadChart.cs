using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using XauatSchedule.Animation;
using XauatSchedule.Core;
using XauatSchedule.Services;

namespace XauatSchedule.Controls;

/// <summary>
/// 学期负载折线（骨架取自 lieflat-charts 的 B2 hairline line）：
/// 发丝折线 + 逐周圆点 + 日历地板刻度 + 峰值标数，同时它就是周次导航器。
/// 拖动时实时切换周次；松手按动量投影决定最终落点。
/// </summary>
public sealed class LoadChart : FrameworkElement
{
    private const double PadLeft = 26, PadRight = 14, PadTop = 16, PadBottom = 20;

    private int[] _load = Array.Empty<int>();
    private int _week = 1;
    private int _currentWeek;
    private Palette _palette = Palette.Get("blue");
    private DrawingGroup? _staticCache;
    private double _cachedWidth, _cachedHeight;
    private bool _reduceMotion;

    private readonly Gesture.VelocityTracker _tracker = new();
    private bool _scrubbing;
    private double _scrubStartX;

    private AnimatedDouble? _markerX;
    private AnimatedDouble? _markerY;
    private double _markerValueX, _markerValueY;
    private bool _popIn;

    public event Action<int>? WeekChanged;

    public LoadChart()
    {
        Height = 84;
        ClipToBounds = true;
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;
    }

    public void Attach(int[] load, int currentWeek, int week, Palette palette, bool reduceMotion)
    {
        _load = load;
        _currentWeek = currentWeek;
        _week = week;
        _palette = palette;
        _reduceMotion = reduceMotion;
        _markerX = new AnimatedDouble(X(0), v => { _markerValueX = v; InvalidateVisual(); }, response: 0.3);
        _markerY = new AnimatedDouble(Y(0), v => { _markerValueY = v; InvalidateVisual(); }, response: 0.3);
        _popIn = true;
        InvalidateStatic();
        UpdateMarkerPosition(animate: false);
    }

    public void SetPalette(Palette p) { _palette = p; InvalidateStatic(); }

    public void SetWeek(int week, bool animate = true)
    {
        _week = Math.Clamp(week, 1, Math.Max(1, _load.Length));
        UpdateMarkerPosition(animate);
    }

    public void SetReducedMotion(bool reduce) => _reduceMotion = reduce;

    private void UpdateMarkerPosition(bool animate)
    {
        var tx = X(_week - 1);
        var ty = Y(_week - 1);
        if (!animate || _reduceMotion || _markerX is null || _markerY is null)
        {
            _markerX?.Jump(tx);
            _markerY?.Jump(ty);
            _markerValueX = tx;
            _markerValueY = ty;
            InvalidateVisual();
        }
        else
        {
            _markerX.Set(tx);
            _markerY.Set(ty);
        }
    }

    // ── 几何 ────────────────────────────────────────────────

    private double Step => _load.Length <= 1 ? 1 : (ActualWidth - PadLeft - PadRight) / (_load.Length - 1);
    private double X(int index) => PadLeft + index * Step;

    private double Y(int index)
    {
        var max = Math.Max(4, _load.DefaultIfEmpty(0).Max());
        var v = _load.Length > index ? _load[index] : 0;
        return ActualHeight - PadBottom - v / (double)max * (ActualHeight - PadTop - PadBottom);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        InvalidateStatic();
        UpdateMarkerPosition(animate: false);
    }

    private void InvalidateStatic()
    {
        _staticCache = null;
        InvalidateVisual();
    }

    // ── 绘制 ────────────────────────────────────────────────

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < 40 || ActualHeight < 30 || _load.Length == 0) return;

        if (_staticCache is null || Math.Abs(_cachedWidth - ActualWidth) > 0.5 || Math.Abs(_cachedHeight - ActualHeight) > 0.5)
            _staticCache = BuildStatic();

        dc.DrawDrawing(_staticCache);
        DrawMarker(dc);
    }

    private DrawingGroup BuildStatic()
    {
        _cachedWidth = ActualWidth;
        _cachedHeight = ActualHeight;

        var g = new DrawingGroup();
        using var ctx = g.Open();
        var gridPen = new Pen(new SolidColorBrush(Palette.WithAlpha(_palette.Data, 0.16)), 1);
        var floorPen = new Pen(new SolidColorBrush(Palette.WithAlpha(_palette.Data, 0.24)), 1);
        var basePen = new Pen(new SolidColorBrush(Palette.WithAlpha(_palette.Text, 0.35)), 1);
        var linePen = new Pen(new SolidColorBrush(_palette.Data), 1.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var max = Math.Max(4, _load.Max());
        var baseY = ActualHeight - PadBottom;

        // 横向参考线
        foreach (var v in new[] { 0, 6, 12, 18 }.Where(v => v <= max))
        {
            var y = baseY - v / (double)max * (baseY - PadTop);
            var pen = v == 0 ? basePen : gridPen;
            if (v != 0) pen.DashStyle = new DashStyle(new double[] { 2, 5 }, 0);
            ctx.DrawLine(pen, new Point(PadLeft - 8, y), new Point(ActualWidth - PadRight, y));
            DrawText(ctx, v.ToString(), new Point(PadLeft - 12, y), _palette.TextMuted, 9.5, TextAlignment.Right, centered: true);
        }

        // 日历地板刻度（每周一根，无论有没有课）
        for (var i = 0; i < _load.Length; i++)
            ctx.DrawLine(floorPen, new Point(X(i), baseY), new Point(X(i), baseY - 6));

        // 月份标签
        var term = App.Current.Store.Data.Term;
        string? lastMonth = null;
        for (var i = 0; i < _load.Length; i++)
        {
            var month = WeekMath.MondayOf(term, i + 1).ToString("MM", CultureInfo.InvariantCulture);
            if (month == lastMonth) continue;
            lastMonth = month;
            DrawText(ctx, $"{int.Parse(month)} 月", new Point(X(i), ActualHeight - 4), _palette.TextMuted, 9.5,
                TextAlignment.Center, centered: true);
        }

        // 折线
        var geo = new StreamGeometry();
        using (var sc = geo.Open())
        {
            sc.BeginFigure(new Point(X(0), Y(0)), false, false);
            for (var i = 1; i < _load.Length; i++)
                sc.LineTo(new Point(X(i), Y(i)), true, false);
        }
        geo.Freeze();
        ctx.DrawGeometry(null, linePen, geo);

        // 圆点：空心 = 该周无课
        var peaks = _load.Select((v, i) => (v, i)).OrderByDescending(t => t.v).Take(2).Select(t => t.i).ToArray();
        for (var i = 0; i < _load.Length; i++)
        {
            var zero = _load[i] == 0;
            var big = peaks.Contains(i) && !zero;
            var r = big ? 4.4 : 3.4;
            var center = new Point(X(i), Y(i));
            var fill = zero ? new SolidColorBrush(_palette.Bg) : new SolidColorBrush(big ? _palette.Hero : _palette.Data);
            var stroke = new Pen(new SolidColorBrush(zero ? _palette.Data2 : _palette.Bg), 1.4);
            if (_popIn && !_reduceMotion)
            {
                // 入场：点阵逐个 pop（token: 8–15ms/个）
                var scale = PopScale(i);
                ctx.PushTransform(new ScaleTransform(scale, scale, center.X, center.Y));
                ctx.DrawEllipse(fill, stroke, center, r, r);
                ctx.Pop();
            }
            else
            {
                ctx.DrawEllipse(fill, stroke, center, r, r);
            }

            if (big)
                DrawText(ctx, _load[i].ToString(), new Point(X(i), Y(i) - 10), _palette.Hero, 10, TextAlignment.Center, centered: true, bold: true);
        }

        _popIn = false;
        return g;
    }

    private static double PopScale(int index)
    {
        // 用固定时间窗近似 token 里的 stagger：入窗后快速到 1
        var t = Math.Clamp((DateTime.UtcNow.Ticks % TimeSpan.TicksPerMinute) / (double)TimeSpan.TicksPerMinute, 0, 1);
        return t < 0.35 ? 0.001 : 1;
    }

    private void DrawMarker(DrawingContext dc)
    {
        if (_load.Length == 0) return;
        var cx = _markerValueX == 0 ? X(_week - 1) : _markerValueX;
        var cy = _markerValueY == 0 ? Y(_week - 1) : _markerValueY;
        var baseY = ActualHeight - PadBottom;

        var heroPen = new Pen(new SolidColorBrush(_palette.Hero), 1.2) { DashStyle = new DashStyle(new double[] { 2, 4 }, 0) };
        dc.DrawLine(heroPen, new Point(cx, Math.Min(cy + 3, baseY)), new Point(cx, baseY));

        var ringPen = new Pen(new SolidColorBrush(Palette.WithAlpha(_palette.Hero, 0.5)), 1.2);
        dc.DrawEllipse(null, ringPen, new Point(cx, cy), 8.5, 8.5);
        dc.DrawEllipse(new SolidColorBrush(_palette.Hero), new Pen(new SolidColorBrush(_palette.Bg), 2), new Point(cx, cy), 5.2, 5.2);

        var label = $"{_load[Math.Clamp(_week - 1, 0, _load.Length - 1)]} 节";
        DrawText(dc, label, new Point(cx, Math.Max(cy - 14, 12)), _palette.Hero, 10.5, TextAlignment.Center, centered: true, bold: true, halo: _palette.Bg);
    }

    private void DrawText(DrawingContext dc, string text, Point at, Color color, double size, TextAlignment align,
        bool centered = false, bool bold = false, Color? halo = null)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Inter, Microsoft YaHei UI, Segoe UI"), FontStyles.Normal,
                bold ? FontWeights.Bold : FontWeights.Medium, FontStretches.Normal),
            size, new SolidColorBrush(color), VisualTreeHelper.GetDpi(this).PixelsPerDip);

        var x = centered ? at.X - ft.Width / 2 : align == TextAlignment.Right ? at.X - ft.Width : at.X;
        var y = at.Y - ft.Height / 2;

        if (halo.HasValue)
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(halo.Value), 3), ft.BuildGeometry(new Point(x, y)));
        dc.DrawText(ft, new Point(x, y));
    }

    // ── 交互：拖动 / 甩动 ───────────────────────────────────

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _scrubbing = true;
        _scrubStartX = e.GetPosition(this).X;
        _tracker.Reset();
        CaptureMouse();
        ScrubTo(_scrubStartX);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_scrubbing) return;
        var x = e.GetPosition(this).X;
        _tracker.Add(x);
        ScrubTo(x);
    }

    private void ScrubTo(double x)
    {
        if (_load.Length == 0) return;
        var index = (int)Math.Round((x - PadLeft) / Math.Max(1, Step));
        var week = Math.Clamp(index + 1, 1, _load.Length);
        if (week != _week)
        {
            _week = week;
            UpdateMarkerPosition(animate: false);
            WeekChanged?.Invoke(week);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_scrubbing) return;
        _scrubbing = false;
        ReleaseMouseCapture();

        // 动量投影：甩动时按预测落点决定最终周
        var v = _tracker.Velocity();
        if (Math.Abs(v) > 260 && _load.Length > 1)
        {
            var projectedX = e.GetPosition(this).X + Gesture.Project(v) * 0.35;
            ScrubTo(projectedX);
        }
    }
}
