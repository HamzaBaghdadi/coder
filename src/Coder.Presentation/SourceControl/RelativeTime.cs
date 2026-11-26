using System.Globalization;

namespace Coder.Presentation.SourceControl;

/// <summary>"just now", "5 minutes ago", "3 days ago": how history and blame show when something happened.</summary>
public static class RelativeTime
{
    public static string Format(DateTimeOffset date, DateTimeOffset now)
    {
        var elapsed = now - date;
        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        return elapsed switch
        {
            { TotalHours: < 1 } => Ago((int)elapsed.TotalMinutes, "minute"),
            { TotalDays: < 1 } => Ago((int)elapsed.TotalHours, "hour"),
            { TotalDays: < 7 } => Ago((int)elapsed.TotalDays, "day"),
            { TotalDays: < 30 } => Ago((int)(elapsed.TotalDays / 7), "week"),
            { TotalDays: < 365 } => Ago(Math.Max(1, (int)(elapsed.TotalDays / 30.44)), "month"),
            _ => Ago((int)(elapsed.TotalDays / 365.25), "year"),
        };
    }

    /// <summary>The full local date and time, for tooltips.</summary>
    public static string Exact(DateTimeOffset date) => date.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);

    private static string Ago(int count, string unit) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} {unit}{(count == 1 ? string.Empty : "s")} ago";
}
