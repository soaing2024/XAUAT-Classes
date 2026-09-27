using System.Text.Json.Serialization;

namespace XauatSchedule.Core;

/// <summary>学期信息。周次以 StartDate 所在周为第 1 周。</summary>
public sealed class TermRecord
{
    public string Name { get; set; } = "2026-2027 学年第一学期";
    /// <summary>第 1 周的周一，yyyy-MM-dd。</summary>
    public string StartDate { get; set; } = "2026-08-31";
    public int TotalWeeks { get; set; } = 20;
    public string School { get; set; } = "西安建筑科技大学";
    public string Campus { get; set; } = "草堂校区";
    public string? StudentId { get; set; }
    /// <summary>学生姓名与行政班（登录后从教务系统取回，仅用于显示）。</summary>
    public string StudentName { get; set; } = "";
    public string AdminClass { get; set; } = "";
}

/// <summary>一节课的节次时间，可在设置里改。</summary>
public sealed class SlotRecord
{
    public int Index { get; set; }
    public string Start { get; set; } = "08:00";
    public string End { get; set; } = "08:45";
}

/// <summary>一门课的一次排课。周次是显式数组，所以单双周、跳周、调课都能表达。</summary>
public sealed class LessonRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string Teacher { get; set; } = "";
    public string Room { get; set; } = "";
    /// <summary>1=周一 … 7=周日。</summary>
    public int Day { get; set; } = 1;
    public int StartSlot { get; set; } = 1;
    public int SlotCount { get; set; } = 2;
    public List<int> Weeks { get; set; } = new();
    /// <summary>主题里的配色键：blue / green / red / ink / amber。</summary>
    public string ColorKey { get; set; } = "blue";
    public string Note { get; set; } = "";
    /// <summary>来源接口给的真实时间（可能长于节次表，如 15:45-21:05），只用于详情展示。</summary>
    public string? SourceTime { get; set; }
    public string? SourceCode { get; set; }
    public string? SourceType { get; set; }
    public string? SourceClass { get; set; }
    /// <summary>true = 用户手动添加/改过，抓取覆盖时默认保留。</summary>
    public bool IsCustom { get; set; }

    [JsonIgnore] public int EndSlot => StartSlot + Math.Max(1, SlotCount) - 1;
    [JsonIgnore] public bool IsSingleWeekEven => Weeks.Count > 0 && Weeks.All(w => w % 2 == 0);
    [JsonIgnore] public bool IsSingleWeekOdd => Weeks.Count > 0 && Weeks.All(w => w % 2 == 1);

    public LessonRecord Clone() => new()
    {
        Id = Id, Name = Name, Teacher = Teacher, Room = Room, Day = Day,
        StartSlot = StartSlot, SlotCount = SlotCount, Weeks = new List<int>(Weeks),
        ColorKey = ColorKey, Note = Note, SourceTime = SourceTime, SourceCode = SourceCode,
        SourceType = SourceType, SourceClass = SourceClass, IsCustom = IsCustom,
    };

    public bool Occupies(int day, int slot) => Day == day && slot >= StartSlot && slot <= EndSlot;
    public bool RunsIn(int week) => Weeks.Contains(week);
}

public enum BackdropMode
{
    /// <summary>自动：优先硬件加速的 DWM 系统背景，不可用则回退真 alpha。</summary>
    Auto = 0,
    /// <summary>丝滑优先：普通窗口 + DWM 系统背景（Mica/Acrylic），硬件加速。</summary>
    Dwm = 1,
    /// <summary>真半透明：分层窗口像素级 alpha（软件渲染，透明度可调）。</summary>
    Layered = 2,
}

public sealed class AppSettings
{
    // —— 外观 ——
    public BackdropMode Backdrop { get; set; } = BackdropMode.Auto;
    public double Opacity { get; set; } = 0.88;          // 分层模式：够透，但深色壁纸上文字仍清晰
    public string AccentKey { get; set; } = "blue";       // blue / green / red / ink
    /// <summary>主题：light / dark —— 现在唯一的主题选项。</summary>
    public string Theme { get; set; } = "light";
    public double CornerRadius { get; set; } = 18;
    public double UiScale { get; set; } = 1.0;

    // —— 窗口行为 ——
    public bool AlwaysOnTop { get; set; }
    public bool DesktopLevel { get; set; }                // 贴桌面（不遮挡其它窗口）
    public bool AutoStart { get; set; }
    public bool CompactView { get; set; }                 // 折叠成小条
    public double? WindowX { get; set; }
    public double? WindowY { get; set; }
    public double WindowWidth { get; set; } = 520;
    public double WindowHeight { get; set; } = 720;

    // —— 课表 ——
    public int LastWeek { get; set; } = 1;
    /// <summary>是否已完成首次登录（本机进入过展示页并且是用抓取数据填充的）。</summary>
    public bool LoggedIn { get; set; }
    /// <summary>密码的 DPAPI 密文（base64）：只能在本机、本 Windows 账户下解开，磁盘上没有明文。</summary>
    public string? ProtectedPassword { get; set; }
    public string? LastStudentId { get; set; }
    /// <summary>上次使用的学期 id（学期列表来自教务系统，刷新时沿用）。</summary>
    public string? LastSemesterId { get; set; }
    public bool ReduceMotion { get; set; }
    public bool MotionProfile { get; set; } = true;       // 显示帧率/性能诊断
}

public sealed class ScheduleFile
{
    public int Version { get; set; } = 1;
    public TermRecord Term { get; set; } = new();
    public List<SlotRecord> Slots { get; set; } = DefaultSlots();
    public List<LessonRecord> Lessons { get; set; } = new();
    public AppSettings Settings { get; set; } = new();
    public string? FetchedAt { get; set; }

    /// <summary>调休周的“真实日期”覆盖表：week → day → yyyy-MM-dd。没有覆盖时按自然周推算。</summary>
    public Dictionary<int, Dictionary<int, string>> ActualDates { get; set; } = new();

    /// <summary>某一周某一天的真实日期（优先用抓取到的日期，否则按自然周推算）。</summary>
    public DateOnly DateOf(int week, int day)
    {
        if (ActualDates.TryGetValue(week, out var days) && days.TryGetValue(day, out var iso)
            && DateOnly.TryParse(iso, out var exact))
            return exact;
        return WeekMath.DateOf(Term, week, day);
    }

    public static List<SlotRecord> DefaultSlots() => new()
    {
        new SlotRecord { Index = 1, Start = "08:30", End = "09:15" },
        new SlotRecord { Index = 2, Start = "09:20", End = "10:05" },
        new SlotRecord { Index = 3, Start = "10:25", End = "11:10" },
        new SlotRecord { Index = 4, Start = "11:15", End = "12:00" },
        new SlotRecord { Index = 5, Start = "14:00", End = "14:45" },
        new SlotRecord { Index = 6, Start = "14:50", End = "15:35" },
        new SlotRecord { Index = 7, Start = "15:45", End = "16:30" },
        new SlotRecord { Index = 8, Start = "16:35", End = "17:20" },
        new SlotRecord { Index = 9, Start = "18:30", End = "19:15" },
        new SlotRecord { Index = 10, Start = "19:20", End = "20:05" },
    };

    public SlotRecord? Slot(int index) => Slots.FirstOrDefault(s => s.Index == index);

    /// <summary>把节次区间转成 "10:25-12:00" 这样的显示文本。</summary>
    public string TimeRange(int startSlot, int count)
    {
        var a = Slot(startSlot);
        var b = Slot(startSlot + Math.Max(1, count) - 1);
        if (a is null || b is null) return "";
        return $"{a.Start}-{b.End}";
    }
}

public static class WeekText
{
    public static readonly string[] DayNames = { "", "周一", "周二", "周三", "周四", "周五", "周六", "周日" };
    public static readonly string[] DayNamesShort = { "", "一", "二", "三", "四", "五", "六", "日" };

    /// <summary>把 [1,2,3,5] 压成 "1-3、5"；单双周另外标注。</summary>
    public static string FormatWeeks(IEnumerable<int> weeks)
    {
        var list = weeks.Distinct().OrderBy(w => w).ToList();
        if (list.Count == 0) return "未设置";
        var parts = new List<string>();
        int start = list[0], prev = list[0];
        for (int i = 1; i <= list.Count; i++)
        {
            var cur = i < list.Count ? list[i] : int.MinValue;
            if (cur == prev + 1) { prev = cur; continue; }
            parts.Add(start == prev ? $"{start}" : $"{start}-{prev}");
            start = prev = cur;
        }
        var text = string.Join("、", parts) + " 周";
        if (list.All(w => w % 2 == 1)) text += "（单周）";
        else if (list.All(w => w % 2 == 0)) text += "（双周）";
        return text;
    }
}
