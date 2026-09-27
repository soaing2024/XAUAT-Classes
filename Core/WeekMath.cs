namespace XauatSchedule.Core;

/// <summary>
/// 周次与日期的换算。所有"今天第几周"的判断都走这里，
/// 保证界面、抓取、提醒用的是同一套规则。
/// </summary>
public static class WeekMath
{
    public static DateOnly ParseStart(string yyyyMmDd)
        => DateOnly.TryParse(yyyyMmDd, out var d) ? d : new DateOnly(2026, 8, 31);

    /// <summary>第 N 周的周一。</summary>
    public static DateOnly MondayOf(TermRecord term, int week)
        => ParseStart(term.StartDate).AddDays((Math.Max(1, week) - 1) * 7);

    public static DateOnly DateOf(TermRecord term, int week, int day)
        => MondayOf(term, week).AddDays(Math.Clamp(day, 1, 7) - 1);

    /// <summary>今天位于第几周；不在学期内返回 null。</summary>
    public static int? CurrentWeek(TermRecord term, DateOnly? today = null)
    {
        var t = today ?? DateOnly.FromDateTime(DateTime.Now);
        var start = ParseStart(term.StartDate);
        var days = t.DayNumber - start.DayNumber;
        if (days < 0) return null;
        var week = days / 7 + 1;
        return week <= term.TotalWeeks ? week : null;
    }

    public static bool IsToday(TermRecord term, int week, int day)
    {
        var t = DateOnly.FromDateTime(DateTime.Now);
        return DateOf(term, week, day) == t;
    }

    /// <summary>"09-28 ~ 10-04" 这样的周范围文本。</summary>
    public static string WeekRange(TermRecord term, int week)
    {
        var a = MondayOf(term, week);
        var b = a.AddDays(6);
        return $"{a:MM-dd} ~ {b:MM-dd}";
    }

    /// <summary>把抓取到的真实时间映射到节次表：命中则返回起始节次，否则返回最接近的一节。</summary>
    public static int SlotForTime(List<SlotRecord> slots, string hhmm)
    {
        if (slots.Count == 0) return 1;
        var target = ToMinutes(hhmm);
        return slots
            .OrderBy(s => Math.Abs(ToMinutes(s.Start) - target))
            .First().Index;
    }

    public static int ToMinutes(string hhmm)
    {
        var parts = hhmm.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return 0;
        return int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m) ? h * 60 + m : 0;
    }

    /// <summary>判断一组课是否在同一格冲突。</summary>
    public static bool Overlaps(LessonRecord a, LessonRecord b)
        => a.Day == b.Day
           && a.StartSlot <= b.EndSlot && b.StartSlot <= a.EndSlot
           && a.Weeks.Intersect(b.Weeks).Any();
}
