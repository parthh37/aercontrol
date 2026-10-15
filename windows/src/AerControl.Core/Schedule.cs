namespace AerControl.Core;

/// <summary>
/// Daily time-of-day window, in minutes after midnight (0–1439).
/// Mirrors ScheduleWindow in macos/Sources/AerKit/ScheduleWindow.swift.
/// </summary>
public sealed record ScheduleWindow(int StartMinutes, int EndMinutes)
{
    public ScheduleWindow() : this(9 * 60, 21 * 60) { }

    public static int MinutesOfDay(DateTime local) => local.Hour * 60 + local.Minute;

    public bool Contains(DateTime local)
    {
        var now = MinutesOfDay(local);
        // TODO: port the macOS ScheduleWindow.contains semantics once decided.
        // Placeholder: same-day window only, end exclusive.
        return now >= StartMinutes && now < EndMinutes;
    }

    public static string Format(int minutes) => $"{minutes / 60:00}:{minutes % 60:00}";
}

public sealed class ScheduleEvaluator(SpraySchedule schedule)
{
    public bool IsDue(DateTime nowLocal, DateTime? lastScheduledSpray)
    {
        if (!schedule.Enabled || schedule.IntervalMinutes <= 0 || !schedule.Window.Contains(nowLocal))
            return false;
        if (lastScheduledSpray is null) return true;
        return (nowLocal - lastScheduledSpray.Value).TotalSeconds >= schedule.IntervalMinutes * 60 - 1;
    }
}
