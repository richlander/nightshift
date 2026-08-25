namespace Octoshift.GitHub;

using System.Globalization;

/// <summary>
/// Parsing for a <c>gh api -i</c> response — the header block, the status line, and the rate-limit and
/// poll-pacing headers GitHub attaches to every REST reply.
/// </summary>
/// <remarks>
/// This is the shared half of the conditional-request pattern the membrane is built on: issue the GET with
/// <c>If-None-Match</c>, and an unchanged resource answers 304 with no body and no rate cost. Extracted
/// here so every source reads a response the same way rather than each growing its own copy.
/// </remarks>
internal static class GhResponse
{
    /// <summary>Splits a <c>gh api -i</c> response into its header block and JSON body at the first blank line.</summary>
    public static (string Headers, string Body) SplitHeadersAndBody(string response)
    {
        if (string.IsNullOrEmpty(response))
        {
            return (string.Empty, string.Empty);
        }

        string normalized = response.Replace("\r\n", "\n", StringComparison.Ordinal);
        int split = normalized.IndexOf("\n\n", StringComparison.Ordinal);
        return split < 0
            ? (normalized, string.Empty)
            : (normalized[..split], normalized[(split + 2)..]);
    }

    /// <summary>Reads the HTTP status code from the status line, falling back to a <c>(HTTP nnn)</c> note in stderr.</summary>
    public static int StatusCode(string headerBlock, string stderr)
    {
        foreach (string line in headerBlock.Split('\n'))
        {
            if (line.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
                {
                    return code;
                }
            }
        }

        foreach (string marker in (string[])["(HTTP ", "HTTP "])
        {
            int at = stderr.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                continue;
            }

            string tail = stderr[(at + marker.Length)..];
            var digits = new string(tail.TakeWhile(char.IsDigit).ToArray());
            if (int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
            {
                return code;
            }
        }

        return 0;
    }

    /// <summary>Reads one header value, case-insensitively.</summary>
    public static string? HeaderValue(string headerBlock, string name)
    {
        foreach (string line in headerBlock.Split('\n'))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && line[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return line[(colon + 1)..].Trim();
            }
        }

        return null;
    }

    /// <summary>Reads a positive integer header, or 0 when absent or unparseable.</summary>
    public static int HeaderInt(string headerBlock, string name)
        => int.TryParse(HeaderValue(headerBlock, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value > 0 ? value : 0;

    /// <summary>True when <c>X-RateLimit-Remaining</c> says the budget for this bucket is spent.</summary>
    public static bool RateBudgetDepleted(string headerBlock)
    {
        string? remaining = HeaderValue(headerBlock, "x-ratelimit-remaining");
        return remaining is not null
            && int.TryParse(remaining, NumberStyles.Integer, CultureInfo.InvariantCulture, out int left)
            && left <= 0;
    }

    /// <summary>Seconds until <c>X-RateLimit-Reset</c>, or 0 when it is absent or already past.</summary>
    public static int SecondsUntilReset(string headerBlock)
    {
        string? reset = HeaderValue(headerBlock, "x-ratelimit-reset");
        if (reset is null || !long.TryParse(reset, NumberStyles.Integer, CultureInfo.InvariantCulture, out long epoch))
        {
            return 0;
        }

        double seconds = DateTimeOffset.FromUnixTimeSeconds(epoch).Subtract(DateTimeOffset.UtcNow).TotalSeconds;
        return seconds > 0 ? (int)Math.Ceiling(seconds) : 0;
    }
}
