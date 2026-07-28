namespace TodoApp.Application.Common.Logging;

/// <summary>
/// Strips control characters from user-controlled values before they reach a log sink.
/// </summary>
/// <remarks>
/// Without this, a request to <c>/foo%0d%0aWARN:+fake+entry</c> writes what looks like a second
/// log line — log forging (CWE-117, CodeQL <c>cs/log-forging</c>). This lives in a shared helper
/// on purpose: the same defect was fixed on <c>main</c> and left unfixed on <c>dapper</c>
/// (review finding M1), so the sanitisation is now one call both branches make and one unit test
/// both branches run.
/// </remarks>
public static class LogSanitizer
{
    /// <summary>
    /// Returns <paramref name="value"/> with CR, LF and other control characters removed, so it
    /// cannot terminate a log line or inject terminal escape sequences.
    /// </summary>
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Fast path: the overwhelming majority of paths contain nothing to strip.
        var needsScrub = false;
        foreach (var c in value)
        {
            if (char.IsControl(c))
            {
                needsScrub = true;
                break;
            }
        }

        if (!needsScrub)
        {
            return value;
        }

        return string.Create(value.Length, value, static (buffer, source) =>
        {
            var written = 0;
            foreach (var c in source)
            {
                if (!char.IsControl(c))
                {
                    buffer[written++] = c;
                }
            }

            buffer[written..].Fill('\0');
        }).TrimEnd('\0');
    }
}
