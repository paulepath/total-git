using System.Globalization;

namespace TotalGit.App.Services;

public static class DateText
{
    /// <summary>"5 min ago", "yesterday", "3 days ago", then the date and time.</summary>
    public static string Relative(DateTimeOffset when)
    {
        var age = DateTimeOffset.Now - when;
        return age.TotalMinutes switch
        {
            < 1 => "just now",
            < 60 => $"{(int)age.TotalMinutes} min ago",
            < 120 => "1 hour ago",
            < 60 * 24 => $"{(int)age.TotalHours} hours ago",
            < 60 * 24 * 2 => "yesterday",
            < 60 * 24 * 7 => $"{(int)age.TotalDays} days ago",
            _ => when.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture),
        };
    }
}
