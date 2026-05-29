using System.Runtime.CompilerServices;

namespace PicoSerDe.Abs;

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
}
