using System.Numerics;
using System.Text;
using PicoSerDe.Core;

namespace PicoJetson;

/// <summary>
/// Scalar I/O helpers for the extended type family (DateTimeOffset, char, Uri,
/// Version, Half, BigInteger, Int128, UInt128, nint) used by
/// source-generator-emitted code. Reads are fail-loud: wrong token types or
/// invalid values throw <see cref="FormatException"/>.
/// </summary>
public static class TypeIo
{
    private static void WriteUtf8(ref JsonWriter w, scoped ReadOnlySpan<byte> utf8) =>
        w.WriteString(utf8);

    // ── writes ──

    public static void WriteDateTimeOffset(ref JsonWriter w, DateTimeOffset value)
    {
        Span<byte> buf = stackalloc byte[40];
        if (!ScalarCodec.TryFormatDateTimeOffset(value, buf, out var n))
            throw new ArgumentException("DateTimeOffset value cannot be formatted.", nameof(value));
        WriteUtf8(ref w, buf[..n]);
    }

    public static void WriteChar(ref JsonWriter w, char value)
    {
        Span<byte> buf = stackalloc byte[8];
        if (!ScalarCodec.TryFormatChar(value, buf, out var n))
            throw new ArgumentException("char value cannot be formatted.", nameof(value));
        WriteUtf8(ref w, buf[..n]);
    }

    public static void WriteUri(ref JsonWriter w, Uri? value) =>
        w.WriteString(Encoding.UTF8.GetBytes(value?.ToString() ?? ""));

    public static void WriteVersion(ref JsonWriter w, Version? value) =>
        w.WriteString(Encoding.UTF8.GetBytes(value?.ToString() ?? ""));

    public static void WriteHalf(ref JsonWriter w, Half value) => w.WriteNumber((double)value);

    public static void WriteBigInteger(ref JsonWriter w, BigInteger value)
    {
        Span<byte> buf = stackalloc byte[64];
        if (!ScalarCodec.TryFormatBigInteger(value, buf, out var n))
            w.WriteString(Encoding.UTF8.GetBytes(value.ToString()));
        else
            w.WriteRawValue(buf[..n]);
    }

    public static void WriteInt128(ref JsonWriter w, Int128 value)
    {
        Span<byte> buf = stackalloc byte[64];
        if (!ScalarCodec.TryFormatInt128(value, buf, out var n))
            w.WriteString(Encoding.UTF8.GetBytes(value.ToString()));
        else
            w.WriteRawValue(buf[..n]);
    }

    public static void WriteUInt128(ref JsonWriter w, UInt128 value)
    {
        Span<byte> buf = stackalloc byte[64];
        if (!ScalarCodec.TryFormatUInt128(value, buf, out var n))
            w.WriteString(Encoding.UTF8.GetBytes(value.ToString()));
        else
            w.WriteRawValue(buf[..n]);
    }

    public static void WriteNInt(ref JsonWriter w, nint value) => w.WriteNumber((long)value);

    // ── reads ──

    private static void RequireString(ref JsonReader r, string typeName)
    {
        if (r.TokenType != TokenType.String)
            throw new FormatException(
                $"Expected a string for {typeName} at offset {r.BytesConsumed}."
            );
    }

    public static DateTimeOffset ReadDateTimeOffset(ref JsonReader r)
    {
        RequireString(ref r, "DateTimeOffset");
        return ScalarCodec.ParseDateTimeOffset(r.ValueSpan);
    }

    public static char ReadChar(ref JsonReader r)
    {
        RequireString(ref r, "char");
        return ScalarCodec.ParseChar(r.ValueSpan);
    }

    public static Uri ReadUri(ref JsonReader r)
    {
        RequireString(ref r, "Uri");
        return ScalarCodec.ParseUri(r.ValueSpan);
    }

    public static Version ReadVersion(ref JsonReader r)
    {
        RequireString(ref r, "Version");
        return ScalarCodec.ParseVersion(r.ValueSpan);
    }

    public static Half ReadHalf(ref JsonReader r)
    {
        if (!r.TryGetFloat64(out var d))
            throw new FormatException($"Expected a number for Half at offset {r.BytesConsumed}.");
        var h = (Half)d;
        if (double.IsFinite(d) && Half.IsInfinity(h))
            throw new FormatException($"Half value is out of range at offset {r.BytesConsumed}.");
        return h;
    }

    private static void RequireIntegerToken(ref JsonReader r, string typeName)
    {
        if (r.TokenType is not (TokenType.Int32 or TokenType.Int64 or TokenType.UInt64))
            throw new FormatException(
                $"Expected an integer for {typeName} at offset {r.BytesConsumed}."
            );
    }

    public static BigInteger ReadBigInteger(ref JsonReader r)
    {
        RequireIntegerToken(ref r, "BigInteger");
        return ScalarCodec.ParseBigInteger(r.ValueSpan);
    }

    public static Int128 ReadInt128(ref JsonReader r)
    {
        RequireIntegerToken(ref r, "Int128");
        return ScalarCodec.ParseInt128(r.ValueSpan);
    }

    public static UInt128 ReadUInt128(ref JsonReader r)
    {
        RequireIntegerToken(ref r, "UInt128");
        return ScalarCodec.ParseUInt128(r.ValueSpan);
    }

    public static nint ReadNInt(ref JsonReader r)
    {
        if (!r.TryGetInt64(out var v))
            throw new FormatException($"Expected an integer for nint at offset {r.BytesConsumed}.");
        return (nint)v;
    }
}