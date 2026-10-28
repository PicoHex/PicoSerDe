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

    /// <summary>Largest text any scalar routed through <see cref="WriteFormattable{T}"/> produces.</summary>
    /// <remarks>
    /// GUID "D" is the longest at 36 characters; ISO-8601 "O" dates are 33, and the longest
    /// constant ("c") <see cref="TimeSpan"/> is 26. One stack buffer serves them all, so the
    /// helper needs no size argument a call site could get wrong — a value that ever outgrows it
    /// is rejected by <c>TryFormat</c> and surfaces as an exception rather than as truncated bytes.
    /// </remarks>
    private const int MaxScalarTextBytes = 40;

    /// <summary>
    /// Writes <paramref name="value"/> as a quoted JSON string, formatted straight into UTF-8.
    /// </summary>
    /// <remarks>
    /// The generated code used to write <c>value.ToString("O")</c>, which allocates the text and
    /// then encodes it — two buffers for a value whose UTF-8 form is pure ASCII. Measuring the
    /// Complex POCO row put its two <c>ToString</c> members (DateTime 21% of the document, Guid
    /// 14%) ahead of every other cost there, so both now format into a span instead: same
    /// characters, no allocation (`.superpowers/sdd/json-elemwrite/report.md`). The text is
    /// quoted by <see cref="JsonWriter.WriteString(ReadOnlySpan{byte})"/>, which also runs the
    /// escape scan — this family cannot produce an escapable byte, but the scan stays on the one
    /// code path every other string takes rather than being special-cased away here.
    /// </remarks>
    public static void WriteFormattable<T>(
        ref JsonWriter w,
        T value,
        scoped ReadOnlySpan<char> format
    )
        where T : IUtf8SpanFormattable
    {
        Span<byte> buf = stackalloc byte[MaxScalarTextBytes];
        if (!value.TryFormat(buf, out var n, format, CultureInfo.InvariantCulture))
            throw new ArgumentException(
                $"{typeof(T).Name} value cannot be formatted as '{format}'.",
                nameof(value)
            );
        WriteUtf8(ref w, buf[..n]);
    }

    public static void WriteDateTimeOffset(ref JsonWriter w, DateTimeOffset value) =>
        WriteFormattable(ref w, value, "O");

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

    public static void WriteHalf(ref JsonWriter w, Half value)
    {
        if (!Half.IsFinite(value))
            // NaN / ±Infinity: let the writer apply its number-handling policy
            // (throw by default, named literals when explicitly allowed).
            w.WriteNumber((double)value);
        else
        {
            // A Half has an 11-bit significand, so widening it to double (or float)
            // spells out the exact binary expansion — 0.1h became
            // 0.0999755859375. Format the Half itself for its shortest round-trip text.
            Span<byte> buf = stackalloc byte[32];
            if (
                !value.TryFormat(
                    buf,
                    out var n,
                    default,
                    System.Globalization.CultureInfo.InvariantCulture
                )
            )
                throw new ArgumentException("Half value cannot be formatted.", nameof(value));
            w.WriteRawValue(buf[..n]);
        }
    }

    public static void WriteBigInteger(ref JsonWriter w, BigInteger value)
    {
        Span<byte> buf = stackalloc byte[64];
        if (ScalarCodec.TryFormatBigInteger(value, buf, out var n))
            w.WriteRawValue(buf[..n]);
        else
            // Values longer than the stack buffer still write as a JSON NUMBER
            // (never a quoted string) so reads round-trip.
            w.WriteRawValue(
                Encoding.UTF8.GetBytes(
                    value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                )
            );
    }

    public static void WriteInt128(ref JsonWriter w, Int128 value)
    {
        Span<byte> buf = stackalloc byte[64];
        if (ScalarCodec.TryFormatInt128(value, buf, out var n))
            w.WriteRawValue(buf[..n]);
        else
            w.WriteRawValue(
                Encoding.UTF8.GetBytes(
                    value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                )
            );
    }

    public static void WriteUInt128(ref JsonWriter w, UInt128 value)
    {
        Span<byte> buf = stackalloc byte[64];
        if (ScalarCodec.TryFormatUInt128(value, buf, out var n))
            w.WriteRawValue(buf[..n]);
        else
            w.WriteRawValue(
                Encoding.UTF8.GetBytes(
                    value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                )
            );
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
