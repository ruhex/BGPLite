namespace BGPLite.Providers;

/// <summary>Replaces control characters and bounds peer- or operator-supplied text before it reaches a log line or an exception message.</summary>
internal static class LogSanitizer
{
    public static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sanitized = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
        {
            sanitized.Append(char.IsControl(ch) ? ' ' : ch);
            if (sanitized.Length >= 200) { sanitized.Append('…'); break; }
        }
        return sanitized.ToString();
    }
}
