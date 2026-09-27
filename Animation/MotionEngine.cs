using System.Diagnostics;
using System.Windows.Media;

namespace XauatSchedule.Animation;

/// <summary>
/// 可中断的弹簧求解器（Apple 的 damping + response 两参数模型）。
///
/// 关键点：
/// 1) 用解析解而不是欧拉积分 —— 结果与帧率无关，60Hz / 120Hz / 掉帧表现一致。
/// 2) 任何时刻都能改 Target，弹簧从"当前值 + 当前速度"继续，反向也不会出现速度突变的硬边。
/// 3) damping = 1.0 为临界阻尼（默认，无过冲）；< 1 用于带惯性的手势收尾。
/// </summary>
public sealed class Spring
{
    public double Value { get; private set; }
    public double Velocity { get; private set; }
    public double Target { get; private set; }

    /// <summary>阻尼比：1.0 无过冲，0.8 轻微回弹（仅给带速度的手势用）。</summary>
    public double Damping { get; init; } = 1.0;

    /// <summary>响应时间（秒）：越小越快。不是时长，弹簧没有固定时长。</summary>
    public double Response { get; init; } = 0.35;

    private const double SettleEpsilon = 0.0008;

    public Spring(double initial, double damping = 1.0, double response = 0.35)
    {
        Value = Target = initial;
        Damping = damping;
        Response = response;
    }

    public bool IsSettled => Math.Abs(Value - Target) < SettleEpsilon && Math.Abs(Velocity) < 0.02;

    /// <summary>改目标：从当前值和当前速度继续，因此天然可中断、可反向。</summary>
    public void SetTarget(double target, double? initialVelocity = null)
    {
        Target = target;
        if (initialVelocity.HasValue) Velocity = initialVelocity.Value;
    }

    public void JumpTo(double value)
    {
        Value = Target = value;
        Velocity = 0;
    }

    /// <summary>推进 dt 秒，返回是否仍在运动。</summary>
    public bool Step(double dt)
    {
        if (IsSettled) { Value = Target; Velocity = 0; return false; }

        var w = 2 * Math.PI / Math.Max(0.02, Response);   // 自然角频率
        var x = Value - Target;                           // 相对目标的位移

        if (Damping >= 1.0 - 1e-6)
        {
            // 临界阻尼解析解
            var c1 = x;
            var c2 = Velocity + w * x;
            var e = Math.Exp(-w * dt);
            Value = Target + (c1 + c2 * dt) * e;
            Velocity = (c2 - w * (c1 + c2 * dt)) * e;
        }
        else
        {
            // 欠阻尼解析解（保留速度，反向时可平滑接管）
            var wd = w * Math.Sqrt(1 - Damping * Damping);
            var e = Math.Exp(-Damping * w * dt);
            var c1 = x;
            var c2 = (Velocity + Damping * w * x) / wd;
            var cos = Math.Cos(wd * dt);
            var sin = Math.Sin(wd * dt);
            Value = Target + e * (c1 * cos + c2 * sin);
            Velocity = e * ((c2 * wd - Damping * w * c1) * cos - (Damping * w * c2 + wd * c1) * sin);
        }

        if (Math.Abs(Value - Target) < SettleEpsilon && Math.Abs(Velocity) < 0.02)
        {
            Value = Target;
            Velocity = 0;
            return false;
        }
        return true;
    }
}

/// <summary>被逐帧驱动的东西。</summary>
public interface IFrameTick
{
    /// <returns>true 表示仍在运动，需要继续占用帧。</returns>
    bool Tick(double dt);
}

/// <summary>
/// 全局帧驱动：整个进程共用一个 CompositionTarget.Rendering 回调，
/// 只有存在活动动画时才挂载，空闲时零开销。
/// </summary>
public static class Animator
{
    private static readonly List<IFrameTick> Ticks = new(32);
    private static readonly List<IFrameTick> PendingAdd = new(8);
    private static readonly List<IFrameTick> PendingRemove = new(8);
    private static bool _hooked;
    private static double _lastSeconds;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    // —— 性能诊断（设置里可开）——
    public static bool ProfileEnabled { get; set; }
    public static double LastFrameMs { get; private set; }
    public static double AverageFrameMs { get; private set; }
    public static double PeakFrameMs { get; private set; }
    public static int ActiveAnimationCount => Ticks.Count;

    public static void Add(IFrameTick tick)
    {
        PendingAdd.Add(tick);
        EnsureHooked();
    }

    public static void Remove(IFrameTick tick)
    {
        PendingRemove.Add(tick);
    }

    /// <summary>立刻在下一帧把动画推进到目标（减少动效 / 首帧布局用）。</summary>
    public static void SettleAll()
    {
        foreach (var t in Ticks.ToArray()) t.Tick(10);
    }

    private static void EnsureHooked()
    {
        if (_hooked) return;
        _hooked = true;
        _lastSeconds = 0;
        CompositionTarget.Rendering += OnFrame;
    }

    private static void OnFrame(object? sender, EventArgs e)
    {
        var now = Clock.Elapsed.TotalSeconds;
        if (sender is RenderingEventArgs args && args.RenderingTime.TotalSeconds > 0)
            now = args.RenderingTime.TotalSeconds;

        // 首帧、切屏、系统休眠后 dt 会异常大：钳到 50ms，避免动画瞬间跳完
        var dt = _lastSeconds <= 0 ? 1.0 / 60 : Math.Min(now - _lastSeconds, 0.05);
        _lastSeconds = now;

        var sw = ProfileEnabled ? Stopwatch.StartNew() : null;

        if (PendingAdd.Count > 0) { Ticks.AddRange(PendingAdd); PendingAdd.Clear(); }
        if (PendingRemove.Count > 0) { foreach (var t in PendingRemove) Ticks.Remove(t); PendingRemove.Clear(); }

        for (var i = Ticks.Count - 1; i >= 0; i--)
        {
            bool alive;
            try { alive = Ticks[i].Tick(dt); }
            catch { alive = false; }
            if (!alive) Ticks.RemoveAt(i);
        }

        if (sw is not null)
        {
            sw.Stop();
            LastFrameMs = sw.Elapsed.TotalMilliseconds;
            AverageFrameMs = AverageFrameMs <= 0 ? LastFrameMs : AverageFrameMs * 0.9 + LastFrameMs * 0.1;
            PeakFrameMs = Math.Max(PeakFrameMs * 0.995, LastFrameMs);
        }

        if (Ticks.Count == 0 && PendingAdd.Count == 0)
        {
            CompositionTarget.Rendering -= OnFrame;
            _hooked = false;
        }
    }
}

/// <summary>
/// 一个被弹簧驱动的 double。赋值即改目标，可随时打断；
/// 每帧只调用一次回调，避免重复布局。
/// </summary>
public sealed class AnimatedDouble : IFrameTick
{
    private readonly Spring _spring;
    private readonly Action<double> _onUpdate;
    private double _lastPushed = double.NaN;

    public AnimatedDouble(double initial, Action<double> onUpdate, double damping = 1.0, double response = 0.35)
    {
        _spring = new Spring(initial, damping, response);
        _onUpdate = onUpdate;
        _lastPushed = initial;
        onUpdate(initial);
    }

    public double Current => _spring.Value;
    public double Target => _spring.Target;
    public double Velocity => _spring.Velocity;

    public void Set(double target, double? velocity = null)
    {
        _spring.SetTarget(target, velocity);
        Animator.Add(this);
    }

    public void Jump(double value)
    {
        _spring.JumpTo(value);
        Push();
    }

    public bool Tick(double dt)
    {
        var alive = _spring.Step(dt);
        Push();
        return alive;
    }

    private void Push()
    {
        if (Math.Abs(_spring.Value - _lastPushed) < 0.0001) return;
        _lastPushed = _spring.Value;
        _onUpdate(_spring.Value);
    }
}

/// <summary>手势辅助：速度追踪、动量投影、边界橡皮筋。</summary>
public static class Gesture
{
    /// <summary>采样指针速度（px/s），只保留最近若干个样本。</summary>
    public sealed class VelocityTracker
    {
        private readonly Queue<(double Time, double Value)> _samples = new();
        private const int Window = 5;

        public void Add(double value)
        {
            var t = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            _samples.Enqueue((t, value));
            while (_samples.Count > Window) _samples.Dequeue();
        }

        public void Reset() => _samples.Clear();

        public double Velocity()
        {
            if (_samples.Count < 2) return 0;
            var first = _samples.Peek();
            var last = _samples.Last();
            var dt = last.Time - first.Time;
            return dt <= 0.001 ? 0 : (last.Value - first.Value) / dt;
        }
    }

    /// <summary>
    /// Apple 的动量投影：根据松手速度预测停下来的位置，
    /// 用它挑最近的吸附点，而不是用松手位置挑。
    /// </summary>
    public static double Project(double velocity, double decelerationRate = 0.998)
        => velocity / 1000.0 * decelerationRate / (1 - decelerationRate);

    /// <summary>拖出边界时的渐进阻力。</summary>
    public static double RubberBand(double overshoot, double dimension, double constant = 0.55)
        => overshoot * dimension * constant / (dimension + constant * Math.Abs(overshoot));

    public static double Clamp(double v, double min, double max) => v < min ? min : v > max ? max : v;

    /// <summary>速度阈值判定方向：用速度而不是位置决定翻页，才跟手。</summary>
    public static bool ShouldAdvance(double velocity, double distance, double distanceThreshold = 56, double velocityThreshold = 420)
        => Math.Abs(velocity) > velocityThreshold || Math.Abs(distance) > distanceThreshold;
}
