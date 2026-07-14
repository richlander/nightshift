namespace Turnstile.Storage;

using System.Text;

/// <summary>Key/value validation and prefix-range helpers (spec §2).</summary>
public static class Keys
{
    public const int MaxKeyBytes = 512;
    public const int MaxValueBytes = 64 * 1024;

    /// <summary>Returns an error message if the key is invalid, otherwise null.</summary>
    public static string? ValidateKey(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return "key must not be empty";
        }

        if (key[0] != '/')
        {
            return "key must begin with '/'";
        }

        if (key.Contains(".."))
        {
            return "key must not contain '..'";
        }

        foreach (char c in key)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                return "key must not contain whitespace or control characters";
            }
        }

        if (Encoding.UTF8.GetByteCount(key) > MaxKeyBytes)
        {
            return $"key must be at most {MaxKeyBytes} bytes";
        }

        return null;
    }

    /// <summary>Returns an error message if the value is too large, otherwise null.</summary>
    public static string? ValidateValue(byte[] value)
        => value.Length > MaxValueBytes ? $"value must be at most {MaxValueBytes} bytes" : null;

    /// <summary>
    /// Computes the exclusive upper bound for a prefix range scan, e.g. "/order/" =&gt; "/order0".
    /// Returns null when the prefix is unbounded (empty, or all high code units).
    /// </summary>
    public static string? PrefixEnd(string prefix)
    {
        if (prefix.Length == 0)
        {
            return null;
        }

        char[] chars = prefix.ToCharArray();
        for (int i = chars.Length - 1; i >= 0; i--)
        {
            if (chars[i] < char.MaxValue)
            {
                chars[i]++;
                return new string(chars, 0, i + 1);
            }
        }

        return null;
    }
}

/// <summary>Thrown when a key or value fails validation; the server maps this to HTTP 400.</summary>
public sealed class TurnstileValidationException(string message) : Exception(message);
