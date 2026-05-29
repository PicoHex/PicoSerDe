namespace PicoJson;

/// <summary>Zero-allocation helpers used by source-generated serializers.</summary>
public static class JsonHelp
{
    /// <summary>Case-insensitive byte-span equality check.</summary>
    public static bool Eq(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            byte x = a[i], y = b[i];
            if (x != y && (x | 0x20) != (y | 0x20)) return false;
        }
        return true;
    }
}
