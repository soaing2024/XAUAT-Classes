using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using XauatSchedule.Services;

namespace XauatSchedule.Core;

/// <summary>
/// 课表的读写与编辑操作。默认落在 %AppData%\XauatSchedule\schedule.json，
/// 首次运行使用内置数据（本项目抓取的真实课表快照）。
/// </summary>
public sealed class ScheduleStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string FilePath { get; }
    /// <summary>本次启动时本地课表文件不存在 = 本机第一次打开，需要走登录页。</summary>
    public bool IsFirstRun { get; private set; }
    public ScheduleFile Data { get; private set; } = new();

    public event Action? Changed;

    public ScheduleStore(string? path = null)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XAUAT-Classes");
        Directory.CreateDirectory(dir);
        FilePath = path ?? Path.Combine(dir, "schedule.json");
    }

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var text = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<ScheduleFile>(text, Json);
                if (loaded is { Lessons.Count: > 0 })
                {
                    Data = loaded;
                    Normalize();
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            // 损坏的文件先备份再用内置数据，绝不丢用户数据
            TryBackupBroken(ex);
        }

        // 没有随包数据：首次运行是一张空课表，必须登录教务系统抓取
        Data = new ScheduleFile();
        IsFirstRun = true;
        Normalize();
        Save();
    }

    private void TryBackupBroken(Exception ex)
    {
        try
        {
            var bak = FilePath + $".broken-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(FilePath, bak, overwrite: true);
            System.Diagnostics.Debug.WriteLine($"课表解析失败，已备份到 {bak}：{ex.Message}");
        }
        catch { /* 备份失败不影响启动 */ }
    }

    public void Save()
    {
        try
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Data, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("保存失败：" + ex.Message);
        }
        Changed?.Invoke();
    }


    private void Normalize()
    {
        Data.Slots = Data.Slots.Count > 0 ? Data.Slots.OrderBy(s => s.Index).ToList() : ScheduleFile.DefaultSlots();
        Data.Settings ??= new AppSettings();
        foreach (var l in Data.Lessons)
        {
            l.Weeks = (l.Weeks ?? new List<int>()).Distinct().OrderBy(w => w).ToList();
            l.SlotCount = Math.Max(1, l.SlotCount);
            l.StartSlot = Math.Clamp(l.StartSlot, 1, Math.Max(1, Data.Slots.Count));
            if (l.SlotCount + l.StartSlot - 1 > Data.Slots.Count)
                l.SlotCount = Math.Max(1, Data.Slots.Count - l.StartSlot + 1);
        }
    }

    // 本应用是纯展示：不提供课程的新增 / 修改 / 删除 / 拖动排课。
    /// <summary>
    /// 把登录后取到的课程上下文写进本地：学期、起止周、真实节次表、学生信息。
    /// 学期起始日由教务系统给的"当前第几周"反推（对齐到周一）。
    /// </summary>
    public void ApplyBootstrap(BootstrapResult boot, string studentId)
    {
        var data = Data;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var mondayThisWeek = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var week = boot.CurrentWeek <= 0 ? 1 : boot.CurrentWeek;

        data.Term.StartDate = mondayThisWeek.AddDays(-(week - 1) * 7).ToString("yyyy-MM-dd");
        data.Term.TotalWeeks = Math.Clamp(boot.WeekCount, 1, 30);
        data.Term.StudentId = studentId;
        data.Term.StudentName = boot.StudentName;
        data.Term.AdminClass = boot.AdminClass;
        var semName = boot.Semesters?.FirstOrDefault(s => s.Id == boot.SemesterId).Name;
        if (!string.IsNullOrEmpty(semName)) data.Term.Name = semName;

        if (boot.Slots is { Count: > 0 }) data.Slots = boot.Slots;

        Data.Settings.LastSemesterId = boot.SemesterId;
        Data.Settings.LastWeek = data.Settings.LastWeek <= 0 ? week : data.Settings.LastWeek;
        Save();
    }

    // ── 凭据（DPAPI 加密，不落明文） ─────────────────────

    public void SaveCredentials(string studentId, string password)
    {
        Data.Term.StudentId = studentId;
        Data.Settings.LastStudentId = studentId;
        Data.Settings.ProtectedPassword = XauatSchedule.Native.Dpapi.Protect(password);
        Data.Settings.LoggedIn = true;
        Save();
    }

    public void ClearCredentials()
    {
        Data.Settings.ProtectedPassword = null;
        Data.Settings.LoggedIn = false;
        Save();
    }

    /// <summary>取出已保存的凭据；没存过或换机器/换账户解不开时返回 false。</summary>
    public bool TryGetCredentials(out string studentId, out string password)
    {
        studentId = Data.Settings.LastStudentId ?? Data.Term.StudentId ?? "";
        password = XauatSchedule.Native.Dpapi.Unprotect(Data.Settings.ProtectedPassword) ?? "";
        return studentId.Length > 0 && password.Length > 0;
    }

    public bool HasLogin => Data.Settings.LoggedIn && !string.IsNullOrEmpty(Data.Settings.ProtectedPassword);

    // 数据只来自两处：内置快照，或教务系统抓取（MergeFetched）。

    /// <summary>
    /// 用抓取结果替换课表：默认保留用户手改过的课（IsCustom），只替换抓取来源的课。
    /// </summary>
    public (int added, int replaced) MergeFetched(IEnumerable<LessonRecord> fetched, string fetchedAt, bool keepCustom = true)
    {
        var incoming = fetched.ToList();
        var keep = keepCustom ? Data.Lessons.Where(l => l.IsCustom).ToList() : new List<LessonRecord>();
        Data.Lessons.Clear();
        Data.Lessons.AddRange(incoming);
        Data.Lessons.AddRange(keep);
        Data.FetchedAt = fetchedAt;
        Save();
        return (incoming.Count, keep.Count);
    }

    /// <summary>本周（第 week 周）要上的课。</summary>
    public List<LessonRecord> ForWeek(int week)
        => Data.Lessons.Where(l => l.RunsIn(week)).OrderBy(l => l.Day).ThenBy(l => l.StartSlot).ToList();

    /// <summary>每周节数，用于负载折线。</summary>
    public int[] WeeklyLoad()
    {
        var total = Data.Term.TotalWeeks;
        var arr = new int[total];
        for (var w = 1; w <= total; w++) arr[w - 1] = ForWeek(w).Count;
        return arr;
    }

}
