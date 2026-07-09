namespace PicoSerDe.Core;

public static class TextHelpers
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';

    public static ReadOnlySpan<byte> TrimEnd(ReadOnlySpan<byte> s)
    {
        int e = s.Length;
        while (e > 0 && (s[e - 1] == (byte)' ' || s[e - 1] == (byte)'\t'))
            e--;
        return s[..e];
    }

    public static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> s)
    {
        int st = 0,
            e = s.Length;
        while (st < e && (s[st] == (byte)' ' || s[st] == (byte)'\t'))
            st++;
        while (e > st && (s[e - 1] == (byte)' ' || s[e - 1] == (byte)'\t'))
            e--;
        return s[st..e];
    }

    /// <summary>Case-insensitive byte-span equality (used by SG deserializers for property name matching).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Eq(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, bool caseSensitive = false)
    {
        if (a.Length != b.Length)
            return false;
        if (caseSensitive)
            return a.SequenceEqual(b);
        for (int i = 0; i < a.Length; i++)
        {
            byte x = a[i],
                y = b[i];
            if (x != y && ((x | 0x20) != (y | 0x20) || (x | 0x20) is < (byte)'a' or > (byte)'z'))
                return false;
        }
        return true;
    }

    // ── Escape-sequence helpers (shared by all format readers) ──

    /// <summary>
    /// Reads <paramref name="count"/> hex digits from <paramref name="src"/>
    /// starting at <paramref name="si"/>, advancing <paramref name="si"/> past
    /// the consumed digits, and returns the decoded integer code-point.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadHexEscape(ReadOnlySpan<byte> src, ref int si, int count)
    {
        int cp = 0;
        for (int j = 0; j < count; j++)
        {
            if (si >= src.Length)
                throw new FormatException("Incomplete escape sequence");
            byte h = src[si++];
            cp <<= 4;
            if (h >= (byte)'0' && h <= (byte)'9')
                cp |= h - (byte)'0';
            else if (h >= (byte)'A' && h <= (byte)'F')
                cp |= h - (byte)'A' + 10;
            else if (h >= (byte)'a' && h <= (byte)'f')
                cp |= h - (byte)'a' + 10;
            else
                throw new FormatException($"Invalid escape char '{(char)h}'");
        }
        return cp;
    }

    /// <summary>
    /// Encodes a Unicode code-point into UTF-8 bytes at <c>buf[di]</c> and
    /// returns the advanced index after the encoded bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int AppendCodepoint(byte[] buf, int di, int cp)
    {
        if (cp < 0x80)
            buf[di++] = (byte)cp;
        else if (cp < 0x800)
        {
            buf[di++] = (byte)(0xC0 | (cp >> 6));
            buf[di++] = (byte)(0x80 | (cp & 0x3F));
        }
        else if (cp < 0x10000)
        {
            buf[di++] = (byte)(0xE0 | (cp >> 12));
            buf[di++] = (byte)(0x80 | ((cp >> 6) & 0x3F));
            buf[di++] = (byte)(0x80 | (cp & 0x3F));
        }
        else
        {
            buf[di++] = (byte)(0xF0 | (cp >> 18));
            buf[di++] = (byte)(0x80 | ((cp >> 12) & 0x3F));
            buf[di++] = (byte)(0x80 | ((cp >> 6) & 0x3F));
            buf[di++] = (byte)(0x80 | (cp & 0x3F));
        }
        return di;
    }
}
