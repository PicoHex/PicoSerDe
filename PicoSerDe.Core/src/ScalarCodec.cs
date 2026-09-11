using System.Globalization;
using System.Numerics;
using System.Text;

namespace PicoSerDe.Core;

/// <summary>
/// UTF-8 text codecs for the extended scalar family (DateTimeOffset, char,
/// Uri, Version, Half, BigInteger, Int128, UInt128) shared by all text-format
/// source generators. Formatting is culture-invariant; parsing is fail-loud —
/// invalid input throws <see cref="FormatException"/>.
/// </summary>
public static class ScalarCodec
{
    private const int MaxChars = 64;

    private static bool TryEncode(ReadOnlySpan<char> chars, Span<byte> dest, out int written)
    {
        var needed = Encoding.UTF8.GetByteCount(chars);
        if (dest.Length < needed)
        {
            written = 0;
            return false;
        }
        written = Encoding.UTF8.GetBytes(chars, dest);
        return true;
    }

    private static int Decode(ReadOnlySpan<byte> utf8, Span<char> chars)
    {
        return Encoding.UTF8.GetChars(utf8, chars);
    }

    // ── DateTimeOffset ──

    /// <summary>Formats with the round-trip ("O") pattern, preserving the UTC offset.</summary>
    public static bool TryFormatDateTimeOffset(
        DateTimeOffset value,
        Span<byte> dest,
        out int written
    )
    {
        Span<char> chars = stackalloc char[MaxChars];
        if (!value.TryFormat(chars, out var n, "O", CultureInfo.InvariantCulture))
        {
            written = 0;
            return false;
        }
        return TryEncode(chars[..n], dest, out written);
    }

    public static DateTimeOffset ParseDateTimeOffset(ReadOnlySpan<byte> utf8)
    {
        Span<char> chars = stackalloc char[MaxChars];
        if (utf8.Length > MaxChars)
            throw new FormatException("DateTimeOffset value is too long.");
        var n = Decode(utf8, chars);
        if (
            n == 0
            || !DateTimeOffset.TryParse(
                chars[..n],
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var value
            )
        )
            throw new FormatException($"Invalid DateTimeOffset value '{Encoding.UTF8.GetString(utf8)}'.");
        return value;
    }

    // ── char ──

    public static bool TryFormatChar(char value, Span<byte> dest, out int written)
    {
        if (value < 0x80)
        {
            if (dest.IsEmpty)
            {
                written = 0;
                return false;
            }
            dest[0] = (byte)value;
            written = 1;
            return true;
        }
        Span<char> chars = stackalloc char[1];
        chars[0] = value;
        return TryEncode(chars, dest, out written);
    }

    public static char ParseChar(ReadOnlySpan<byte> utf8)
    {
        Span<char> chars = stackalloc char[4];
        var n = Decode(utf8, chars);
        if (n != 1)
            throw new FormatException(
                $"Expected a single-character string, got '{Encoding.UTF8.GetString(utf8)}'."
            );
        return chars[0];
    }

    // ── Uri / Version ──

    public static bool TryFormatUri(Uri value, Span<byte> dest, out int written) =>
        TryEncode(value.ToString(), dest, out written);

    public static Uri ParseUri(ReadOnlySpan<byte> utf8)
    {
        Span<char> chars = stackalloc char[Math.Max(256, utf8.Length)];
        var n = Decode(utf8, chars);
        if (!Uri.TryCreate(new string(chars[..n]), UriKind.RelativeOrAbsolute, out var value))
            throw new FormatException($"Invalid Uri value '{Encoding.UTF8.GetString(utf8)}'.");
        return value;
    }

    public static bool TryFormatVersion(Version value, Span<byte> dest, out int written) =>
        TryEncode(value.ToString(), dest, out written);

    public static Version ParseVersion(ReadOnlySpan<byte> utf8)
    {
        Span<char> chars = stackalloc char[MaxChars];
        var n = Decode(utf8, chars);
        if (!Version.TryParse(chars[..n], out var value))
            throw new FormatException($"Invalid Version value '{Encoding.UTF8.GetString(utf8)}'.");
        return value;
    }

    // ── Half ──

    public static bool TryFormatHalf(Half value, Span<byte> dest, out int written)
    {
        Span<char> chars = stackalloc char[MaxChars];
        if (!((double)value).TryFormat(chars, out var n, default, CultureInfo.InvariantCulture))
        {
            written = 0;
            return false;
        }
        return TryEncode(chars[..n], dest, out written);
    }

    public static Half ParseHalf(ReadOnlySpan<byte> utf8)
    {
        Span<char> chars = stackalloc char[MaxChars];
        var n = Decode(utf8, chars);
        if (
            !double.TryParse(
                chars[..n],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var d
            )
        )
            throw new FormatException($"Invalid Half value '{Encoding.UTF8.GetString(utf8)}'.");
        var h = (Half)d;
        if (double.IsFinite(d) && Half.IsInfinity(h))
            throw new FormatException($"Half value '{Encoding.UTF8.GetString(utf8)}' is out of range.");
        return h;
    }

    // ── BigInteger / Int128 / UInt128 ──

    public static bool TryFormatBigInteger(BigInteger value, Span<byte> dest, out int written)
    {
        Span<char> chars = stackalloc char[MaxChars];
        if (!value.TryFormat(chars, out var n, default, CultureInfo.InvariantCulture))
        {
            written = 0;
            return false;
        }
        return TryEncode(chars[..n], dest, out written);
    }

    public static BigInteger ParseBigInteger(ReadOnlySpan<byte> utf8)
    {
        Span<char> chars = stackalloc char[MaxChars];
        if (utf8.Length > MaxChars)
            throw new FormatException("BigInteger value is too long.");
        var n = Decode(utf8, chars);
        if (
            !BigInteger.TryParse(
                chars[..n],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var value
            )
        )
            throw new FormatException($"Invalid BigInteger value '{Encoding.UTF8.GetString(utf8)}'.");
        return value;
    }

    public static bool TryFormatInt128(Int128 value, Span<byte> dest, out int written)
    {
        Span<char> chars = stackalloc char[MaxChars];
        if (!value.TryFormat(chars, out var n, default, CultureInfo.InvariantCulture))
        {
            written = 0;
            return false;
        }
        return TryEncode(chars[..n], dest, out written);
    }

    public static Int128 ParseInt128(ReadOnlySpan<byte> utf8)
    {
        Span<char> chars = stackalloc char[MaxChars];
        var n = Decode(utf8, chars);
        if (
            !Int128.TryParse(
                chars[..n],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var value
            )
        )
            throw new FormatException($"Invalid Int128 value '{Encoding.UTF8.GetString(utf8)}'.");
        return value;
    }

    public static bool TryFormatUInt128(UInt128 value, Span<byte> dest, out int written)
    {
        Span<char> chars = stackalloc char[MaxChars];
        if (!value.TryFormat(chars, out var n, default, CultureInfo.InvariantCulture))
        {
            written = 0;
            return false;
        }
        return TryEncode(chars[..n], dest, out written);
    }

    public static UInt128 ParseUInt128(ReadOnlySpan<byte> utf8)
    {
        Span<char> chars = stackalloc char[MaxChars];
        var n = Decode(utf8, chars);
        if (
            !UInt128.TryParse(
                chars[..n],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var value
            )
        )
            throw new FormatException($"Invalid UInt128 value '{Encoding.UTF8.GetString(utf8)}'.");
        return value;
    }

    // ── nint ──

    public static bool TryFormatNInt(nint value, Span<byte> dest, out int written)
    {
        Span<char> chars = stackalloc char[MaxChars];
        if (!((long)value).TryFormat(chars, out var n, default, CultureInfo.InvariantCulture))
        {
            written = 0;
            return false;
        }
        return TryEncode(chars[..n], dest, out written);
    }

    public static nint ParseNInt(ReadOnlySpan<byte> utf8)
    {
        Span<char> chars = stackalloc char[MaxChars];
        var n = Decode(utf8, chars);
        if (!long.TryParse(chars[..n], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            throw new FormatException($"Invalid nint value '{Encoding.UTF8.GetString(utf8)}'.");
        return (nint)v;
    }
}