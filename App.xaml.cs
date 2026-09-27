using System.IO;
using System.Text;
using System.Windows;
using XauatSchedule.Core;
using XauatSchedule.Native;
using XauatSchedule.Services;
using XauatSchedule.Views;

namespace XauatSchedule;

public partial class App : Application
{
    private const string MutexName = "XAUAT-Classes.SingleInstance.v1";

    private Mutex? _mutex;
    private TrayIcon? _tray;
    private MainWindow? _main;

    public ScheduleStore Store { get; private set; } = new();
    public static new App Current => (App)Application.Current;

    /// <summary>启动参数：--tray 直接进托盘（自启用）、--selftest 跑自检、--mode=dwm|layered 强制渲染模式。</summary>
    private static string[] Args => Environment.GetCommandLineArgs().Skip(1).ToArray();

    protected override void OnStartup(StartupEventArgs e)
    {
        // 任何未处理异常都落盘，方便排查（窗口应用没有控制台）
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash(args.Exception);
            MessageBox.Show("出现了一个问题：\n" + args.Exception.Message, "XAUAT-Classes", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogCrash(args.ExceptionObject as Exception);

        base.OnStartup(e);

        // 单实例：第二次启动把已有窗口唤到前台
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "xauat-boot.txt"), $"[{DateTime.Now:HH:mm:ss.fff}] OnStartup args={string.Join("|", Args)}\n", Encoding.UTF8); } catch { }

        Boot("mutex");
        _mutex = new Mutex(true, MutexName, out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("XAUAT-Classes 已经在运行了（看右下角托盘图标）。", "已在运行",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Boot("store.new");
        Store = new ScheduleStore();
        Boot("store.load");
        Store.Load();

        // 自动化验收用：--test-autostart on|off 只改注册表后退出（支持 = 与空格两种写法）
        var autoArg = Args.FirstOrDefault(a => a.StartsWith("--test-autostart", StringComparison.OrdinalIgnoreCase));
        if (autoArg is not null && !autoArg.Contains('='))
        {
            var idx = Array.IndexOf(Args, autoArg);
            if (idx + 1 < Args.Length) autoArg += "=" + Args[idx + 1];
        }
        if (autoArg is not null)
        {
            var on = autoArg.EndsWith("on", StringComparison.OrdinalIgnoreCase);
            AutoStart.TrySet(on, out var d);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "xauat-autostart.txt"),
                $"设置={(on ? "开" : "关")} 结果={AutoStart.IsEnabled()} 详情={d}", Encoding.UTF8);
            Shutdown();
            return;
        }

        if (Args.Contains("--selftest"))
        // 自动化验收用：--test-fetch 走真实登录+抓取（凭据从环境变量读，不落盘）
        Boot("branch.testfetch");
        if (Args.Contains("--test-fetch"))
        {
            // 放到线程池跑：UI 线程上阻塞等待 async 会死锁（继续执行要靠 dispatcher）
        Boot("testfetch.taskrun");
            Task.Run(RunFetchTest).GetAwaiter().GetResult();
            Shutdown();
            return;
        }

        if (Args.Contains("--selftest"))
        {
            RunSelfTest();
            Shutdown();
            return;
        }

        Boot("theme");
        ThemeService.Apply(Palette.Get(Store.Data.Settings.AccentKey));

        var mode = ResolveBackdropMode();
        _main = new MainWindow(Store, mode);
        MainWindow = _main;

        _main.Closed += (_, _) => ExitApp();

        if (!Args.Contains("--tray"))
            _main.Show();
        else
            _main.Hide();

        SetupTray();
    }

    /// <summary>
    /// 决定用哪种背景：命令行 > 设置 > 自动。
    /// 自动策略：Win11 优先走 DWM 系统背景（硬件加速），不满足条件才用分层 alpha。
    /// </summary>
    private BackdropMode ResolveBackdropMode()
    {
        var arg = Args.FirstOrDefault(a => a.StartsWith("--mode=", StringComparison.OrdinalIgnoreCase));
        if (arg is not null)
        {
            var v = arg.Split('=', 2)[1].ToLowerInvariant();
            return v switch
            {
                "layered" => BackdropMode.Layered,
                "dwm" => BackdropMode.Dwm,
                _ => BackdropMode.Auto,
            };
        }

        var setting = Store.Data.Settings.Backdrop;
        if (setting != BackdropMode.Auto) return setting;

        // 在本机实测：Win11 的系统背景（Mica）会把面板洗得很白，反而没有"半透明"的观感；
        // 所以自动模式默认用分层 alpha（已实测透光、文字可控），想要硬件加速再手动切。
        return BackdropMode.Layered;
    }

    private void SetupTray()
    {
        if (_main is null) return;
        _tray = new TrayIcon(_main, Store.Data.Settings.AlwaysOnTop, AutoStart.IsEnabled());
        _tray.ExitRequested += ExitApp;
        _tray.ResetPositionRequested += () => _main?.ResetPosition();
        _tray.TopMostToggled += on => _main?.ApplyTopMost(on, notify: true);
        _tray.AutoStartToggled += on =>
        {
            AutoStart.TrySet(on, out var detail);
            Store.Data.Settings.AutoStart = on;
            Store.Save();
            _tray?.ShowBalloon("开机自启", on ? "已开启（可在任务管理器→启动应用里看到）" : "已关闭");
            _main?.SyncSettingsUi();
            _ = detail;
        };
    }

    public void SyncTray(bool topMost, bool autoStart) => _tray?.SyncState(topMost, autoStart);

    public void Notify(string title, string text) => _tray?.ShowBalloon(title, text);

    public void ExitApp()
    {
        try
        {
            _main?.PersistWindowPlacement();
            Store.Save();
        }
        catch { }
        _tray?.Dispose();
        _main?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    // ── 自检（无界面，结果写文件；便于自动化验收） ──────────
    /// <summary>真实抓取自检：登录 → 抓 20 周 → 结果写临时文件（凭据来自环境变量，不落盘）。</summary>
    private async Task RunFetchTest()
    {
        Boot("runfetchtest.enter");
        var path = Path.Combine(Path.GetTempPath(), "xauat-fetch-test.txt");
        File.WriteAllText(path, "", Encoding.UTF8);
        void Line(string s)
        {
            try { File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {s}\n", Encoding.UTF8); } catch { }
        }
        try
        {
            var user = Environment.GetEnvironmentVariable("XAUAT_TEST_USER") ?? "";
            var pass = Environment.GetEnvironmentVariable("XAUAT_TEST_PASS") ?? "";
            if (user.Length == 0 || pass.Length == 0)
            {
                Line("缺少环境变量 XAUAT_TEST_USER / XAUAT_TEST_PASS");
            }
            else
            {
                using var client = new XauatClient();
                XauatClient.Trace = Line;
                var login = await client.LoginAsync(user, pass);
                Line($"登录: success={login.Success} needCaptcha={login.NeedsCaptcha} 验证码图片字节={login.CaptchaImage?.Length ?? 0} msg={login.Message}");
                if (login.Success)
                {
                    var known = Store.Data.Lessons.Select(l => l.Id.Split('-')[0])
                        .Where(s => int.TryParse(s, out _)).Select(int.Parse).Distinct().ToArray();
                    Line($"本地已知 lessonIds: {known.Length} 个");
                    var boot = await client.BootstrapAsync();
                    Line($"引导: success={boot.Success} msg={boot.Message} stdPersonId={boot.StdPersonId} 学期={boot.SemesterId} 当前周={boot.CurrentWeek} 周数={boot.WeekCount} lessonIds={boot.LessonIds?.Length ?? 0} 节次={boot.Slots?.Count ?? 0} 学生={boot.StudentName} 班级={boot.AdminClass}");

                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var res = boot.Success
                        ? await client.FetchAllAsync(Store.Data.Term, boot.SemesterId, boot.StdPersonId, boot.LessonIds!, boot.Slots!,
                            new Progress<(int week, int total, string message)>(p => Line($"  {p.message}")))
                        : new FetchResult(false, "引导失败，已跳过抓取", new(), "");
                    sw.Stop();
                    Line($"抓取: success={res.Success} msg={res.Message} 耗时={sw.ElapsedMilliseconds}ms");
                    var weeks = res.Lessons.SelectMany(l => l.Weeks).Distinct().OrderBy(w => w).ToArray();
                    Line($"周次范围: {string.Join(",", weeks)}");
                    Line($"课次: {res.Lessons.Count} 条 / 总节次 {res.Lessons.Sum(l => l.Weeks.Count)}");
                    var first = res.Lessons.FirstOrDefault();
                    Line($"示例: {first?.Name} @ {first?.Room} 周次 {string.Join(",", first?.Weeks ?? new List<int>())}");
                }
            }
            Line("结果: OK");
        }
        catch (Exception ex)
        {
            Line("结果: FAIL " + ex);
        }
    }

    private static void Boot(string tag)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "xauat-boot.txt"), $"[{DateTime.Now:HH:mm:ss.fff}] {tag}\n", Encoding.UTF8); } catch { }
    }

    private static void LogCrash(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "xauat-error.txt"),
                $"[{DateTime.Now:HH:mm:ss}] {ex}\n\n", Encoding.UTF8);
        }
        catch { }
    }

    private void RunSelfTest()
    {
        var log = new StringBuilder();
        void Line(string s) => log.AppendLine(s);

        try
        {
            var s = Store.Data;
            Line($"数据文件: {Store.FilePath}");
            Line($"学期: {s.Term.Name} 起始 {s.Term.StartDate} 共 {s.Term.TotalWeeks} 周");
            Line($"课程条数: {s.Lessons.Count}");
            Line($"节次表: {s.Slots.Count} 节");
            var load = Store.WeeklyLoad();
            Line($"每周节数: {string.Join(",", load)}");
            Line($"当前周: {WeekMath.CurrentWeek(s.Term)?.ToString() ?? "不在学期内"}");

            // 加密自检：同样的输入两次结果必须不同（随机 IV），长度符合 base64
            var a = XauatClient.EncryptPassword("test", "zK5xby9J45mhqFZP");
            var b = XauatClient.EncryptPassword("test", "zK5xby9J45mhqFZP");
            Line($"加密: len={a.Length} 随机IV={(a != b ? "是" : "否")} 可解析={Convert.TryFromBase64String(a, new byte[a.Length], out _)}");

            // 周次换算自检
            var term = s.Term;
            Line($"第1周周一: {WeekMath.MondayOf(term, 1):yyyy-MM-dd}");
            Line($"第5周范围: {WeekMath.WeekRange(term, 5)}");
            Line($"日期→节次: 10:25 → 第{WeekMath.SlotForTime(s.Slots, "10:25")}节, 16:35 → 第{WeekMath.SlotForTime(s.Slots, "16:35")}节");

            Line($"展示模式: 只读（无编辑功能） 课次总数: {s.Lessons.Sum(l => l.Weeks.Count)}");

            Line("结果: OK");
        }
        catch (Exception ex)
        {
            Line("结果: FAIL " + ex);
        }

        var outPath = Path.Combine(Path.GetTempPath(), "xauat-selftest.txt");
        File.WriteAllText(outPath, log.ToString(), Encoding.UTF8);
    }
}
