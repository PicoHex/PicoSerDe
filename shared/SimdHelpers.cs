using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace PicoSerDe.Core;

internal static class SimdHelpers
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SkipWhitespace(ReadOnlySpan<byte> data, int position)
    {
        int len = data.Length;

        // SIMD: process 16 bytes at a time
        if (Vector128.IsHardwareAccelerated)
        {
            var spaceVec = Vector128.Create((byte)0x20);
            var tabVec = Vector128.Create((byte)0x09);
            var newlineVec = Vector128.Create((byte)0x0A);
            var crVec = Vector128.Create((byte)0x0D);

            while (position + 16 <= len)
            {
                ref readonly var src = ref data[position];
                var chunk = Vector128.LoadUnsafe(in src);
                var isWs =
                    Vector128.Equals(chunk, spaceVec)
                    | Vector128.Equals(chunk, tabVec)
                    | Vector128.Equals(chunk, newlineVec)
                    | Vector128.Equals(chunk, crVec);
                var bits = isWs.ExtractMostSignificantBits();

                if (bits == 0xFFFF)
                {
                    position += 16;
                    continue;
                }

                position += BitOperations.TrailingZeroCount(~bits);
                return position;
            }
        }

        // Scalar fallback
        while (
            position < len
            && data[position] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r'
        )
        {
            position++;
        }
        return position;
    }
}
