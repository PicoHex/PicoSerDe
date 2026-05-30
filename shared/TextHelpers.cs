using System.Runtime.CompilerServices;

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
        int st = 0, e = s.Length;
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
            byte x = a[i], y = b[i];
            if (x != y && ((x | 0x20) != (y | 0x20) || (x | 0x20) is < (byte)'a' or > (byte)'z'))
                return false;
        }
        return true;
    }
}
