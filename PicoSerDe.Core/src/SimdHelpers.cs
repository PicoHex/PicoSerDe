namespace PicoSerDe.Core;

public static class SimdHelpers
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SkipWhitespace(ReadOnlySpan<byte> data, int position)
    {
        int len = data.Length;

        // SIMD: process 64 bytes at a time (AVX-512 / SVE 512)
        if (Vector512.IsHardwareAccelerated)
        {
            var spaceVec = Vector512.Create((byte)0x20);
            var tabVec = Vector512.Create((byte)0x09);
            var newlineVec = Vector512.Create((byte)0x0A);
            var crVec = Vector512.Create((byte)0x0D);

            while (position + 64 <= len)
            {
                ref readonly var src = ref data[position];
                var chunk = Vector512.LoadUnsafe(in src);
                var isWs =
                    Vector512.Equals(chunk, spaceVec)
                    | Vector512.Equals(chunk, tabVec)
                    | Vector512.Equals(chunk, newlineVec)
                    | Vector512.Equals(chunk, crVec);
                var bits = isWs.ExtractMostSignificantBits();

                if (bits == ulong.MaxValue)
                {
                    position += 64;
                    continue;
                }

                position += BitOperations.TrailingZeroCount(~bits);
                return position;
            }
        }
        // SIMD: process 32 bytes at a time (AVX2 / SVE 256)
        else if (Vector256.IsHardwareAccelerated)
        {
            var spaceVec = Vector256.Create((byte)0x20);
            var tabVec = Vector256.Create((byte)0x09);
            var newlineVec = Vector256.Create((byte)0x0A);
            var crVec = Vector256.Create((byte)0x0D);

            while (position + 32 <= len)
            {
                ref readonly var src = ref data[position];
                var chunk = Vector256.LoadUnsafe(in src);
                var isWs =
                    Vector256.Equals(chunk, spaceVec)
                    | Vector256.Equals(chunk, tabVec)
                    | Vector256.Equals(chunk, newlineVec)
                    | Vector256.Equals(chunk, crVec);
                var bits = isWs.ExtractMostSignificantBits();

                if (bits == uint.MaxValue)
                {
                    position += 32;
                    continue;
                }

                position += BitOperations.TrailingZeroCount(~bits);
                return position;
            }
        }
        // SIMD: process 16 bytes at a time (SSE2 / NEON baseline)
        else if (Vector128.IsHardwareAccelerated)
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
            position < len && data[position] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r'
        )
        {
            position++;
        }
        return position;
    }

    /// <summary>SIMD-accelerated skip of spaces and tabs only (newlines remain significant). Used by INI reader.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SkipSpacesAndTabs(ReadOnlySpan<byte> data, int position)
    {
        int len = data.Length;

        if (Vector512.IsHardwareAccelerated)
        {
            var spaceVec = Vector512.Create((byte)0x20);
            var tabVec = Vector512.Create((byte)0x09);

            while (position + 64 <= len)
            {
                ref readonly var src = ref data[position];
                var chunk = Vector512.LoadUnsafe(in src);
                var isWs = Vector512.Equals(chunk, spaceVec) | Vector512.Equals(chunk, tabVec);
                var bits = isWs.ExtractMostSignificantBits();

                if (bits == ulong.MaxValue)
                {
                    position += 64;
                    continue;
                }

                position += BitOperations.TrailingZeroCount(~bits);
                return position;
            }
        }
        else if (Vector256.IsHardwareAccelerated)
        {
            var spaceVec = Vector256.Create((byte)0x20);
            var tabVec = Vector256.Create((byte)0x09);

            while (position + 32 <= len)
            {
                ref readonly var src = ref data[position];
                var chunk = Vector256.LoadUnsafe(in src);
                var isWs = Vector256.Equals(chunk, spaceVec) | Vector256.Equals(chunk, tabVec);
                var bits = isWs.ExtractMostSignificantBits();

                if (bits == uint.MaxValue)
                {
                    position += 32;
                    continue;
                }

                position += BitOperations.TrailingZeroCount(~bits);
                return position;
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            var spaceVec = Vector128.Create((byte)0x20);
            var tabVec = Vector128.Create((byte)0x09);

            while (position + 16 <= len)
            {
                ref readonly var src = ref data[position];
                var chunk = Vector128.LoadUnsafe(in src);
                var isWs = Vector128.Equals(chunk, spaceVec) | Vector128.Equals(chunk, tabVec);
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

        while (position < len && data[position] is (byte)' ' or (byte)'\t')
        {
            position++;
        }
        return position;
    }
}
