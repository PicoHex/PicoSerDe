namespace PicoSerDe.Core;

/// <summary>
/// The bulk ("fast path") scan of a flow-style scalar array — <c>[1, 42, -7]</c>,
/// <c>[true, false]</c> — over a raw span, shared by the three text readers that tokenize
/// the SAME grammar: JSON, TOML inline arrays and YAML flow sequences.
/// <para>
/// The three formats differ in how they <i>reach</i> the element list and in almost nothing
/// about the list itself: the separator is <c>,</c>, the terminator is <c>]</c>, an element
/// is a bare <c>-?digits</c> or the literal <c>true</c>/<c>false</c>, and whitespace may
/// appear anywhere between them. Reaching the list is the reader's own business (a JSON
/// <c>Read()</c>, a TOML <c>Read()</c> pair, a YAML raw position); the scan is this file's.
/// </para>
/// <para>
/// <b>Contract.</b> Returns the element count written to <paramref name="dest"/>, or 0 to
/// mean "fall back to the validated reader". <paramref name="position"/> advances ONLY on a
/// successful scan: every bail-out (sequence mode, end of buffer, a byte that is not part of
/// the grammar, integer overflow) leaves it untouched, so a caller never has to rewind. The
/// scan also stops successfully — leaving <c>]</c> unconsumed — once <paramref name="dest"/>
/// is full, which is what makes a partially-sized caller buffer resumable.
/// </para>
/// <para>
/// <b>Why a static class and not a generic/abstract shape.</b> The callers are
/// <c>ref struct</c> readers: a <c>delegate</c> element parser, a closure or a boxed
/// strategy object would capture a span (illegal), allocate (a struct that must not escape)
/// or defeat AOT's static analysis. Plain static methods over spans keep every call site
/// devirtualized and inlineable, and the element grammar here needs no strategy anyway:
/// the only per-format variation that existed was between the two integer overflow checks,
/// and they are kept as two methods on purpose (see <see cref="TryReadInt64Array"/>).
/// </para>
/// <para>
/// Internal, not public: <c>PicoSerDe.Core</c> grants <c>InternalsVisibleTo</c> to
/// <c>PicoJetson</c>, <c>PicoToml</c>, <c>PicoYaml</c> and <c>PicoIni</c>
/// (<c>PicoSerDe.Core.csproj</c>), so this is invisible to consumers while each format keeps
/// the public <c>TryRead*ArrayFast</c> method its own callers (and generated code) use. The
/// public surface does not move: <c>api/*.public.txt</c> is unchanged by this file existing.
/// </para>
/// </summary>
internal static class FastArrayReaders
{
    /// <summary>
    /// Scans <c>-?[0-9]+</c> elements into <paramref name="dest"/>.
    /// <para>
    /// Overflow is checked <i>after</i> the accumulate, against <see cref="int.MaxValue"/>, with
    /// one exception: <c>-2147483648</c> is a legal int32, so a negative sign buys exactly one
    /// step past the maximum. The check runs every iteration, so <c>v</c> cannot overflow the
    /// <see cref="long"/> accumulator that holds the digits before the bound is applied.
    /// </para>
    /// </summary>
    /// <returns>Element count, or 0 to fall back to the validated reader.</returns>
    internal static int TryReadInt32Array(
        ReadOnlySpan<byte> data,
        ref int position,
        bool isSequence,
        scoped Span<int> dest
    )
    {
        if (isSequence)
            return 0;
        var p = position;
        var d = data;
        var len = d.Length;
        if (p >= len)
            return 0;

        int count = 0;
        while (p < len && count < dest.Length)
        {
            byte b = d[p];
            if (b <= 32)
            {
                p = SimdHelpers.SkipWhitespace(d, p);
                continue;
            }
            if (b == (byte)']')
            {
                p++;
                break;
            }
            if (b == (byte)',')
            {
                p++;
                continue;
            }
            bool neg = false;
            if (b == (byte)'-')
            {
                neg = true;
                p++;
                if (p >= len)
                    return 0;
                b = d[p];
            }
            if (b < (byte)'0' || b > (byte)'9')
                return 0;
            long v = 0;
            do
            {
                int digit = b - (byte)'0';
                v = v * 10 + digit;
                // -2147483648 is a legal int32: allow one past int.MaxValue
                // when the sign is negative.
                if (v > int.MaxValue && !(neg && v == (long)int.MaxValue + 1))
                    return 0; // overflow — fall back to the validated reader
                p++;
                if (p >= len)
                    break;
                b = d[p];
            } while (b >= (byte)'0' && b <= (byte)'9');
            dest[count++] = (int)(neg ? -v : v);
        }
        position = p;
        return count;
    }

    /// <summary>
    /// Scans <c>-?[0-9]+</c> elements into <paramref name="dest"/>.
    /// <para>
    /// Overflow is checked <i>before</i> the accumulate (<c>v &gt; (long.MaxValue - digit) / 10</c>),
    /// which is deliberately NOT the int32 shape above: it is the exact pre-multiply guard, so it
    /// rejects <c>-9223372036854775808</c> (the magnitude is one past <see cref="long.MaxValue"/>,
    /// and there is no "negative buys one more" branch here). That asymmetry is the wire
    /// behaviour all three readers had before this file existed; it is preserved byte for byte.
    /// </para>
    /// </summary>
    /// <returns>Element count, or 0 to fall back to the validated reader.</returns>
    internal static int TryReadInt64Array(
        ReadOnlySpan<byte> data,
        ref int position,
        bool isSequence,
        scoped Span<long> dest
    )
    {
        if (isSequence)
            return 0;
        var p = position;
        var d = data;
        var len = d.Length;
        if (p >= len)
            return 0;

        int count = 0;
        while (p < len && count < dest.Length)
        {
            byte b = d[p];
            if (b <= 32)
            {
                p = SimdHelpers.SkipWhitespace(d, p);
                continue;
            }
            if (b == (byte)']')
            {
                p++;
                break;
            }
            if (b == (byte)',')
            {
                p++;
                continue;
            }
            bool neg = false;
            if (b == (byte)'-')
            {
                neg = true;
                p++;
                if (p >= len)
                    return 0;
                b = d[p];
            }
            if (b < (byte)'0' || b > (byte)'9')
                return 0;
            long v = 0;
            do
            {
                int digit = b - (byte)'0';
                if (v > (long.MaxValue - digit) / 10)
                    return 0; // overflow — fall back to the validated reader
                v = v * 10 + digit;
                p++;
                if (p >= len)
                    break;
                b = d[p];
            } while (b >= (byte)'0' && b <= (byte)'9');
            dest[count++] = neg ? -v : v;
        }
        position = p;
        return count;
    }

    /// <summary>
    /// Scans the two literals <c>true</c> / <c>false</c> into <paramref name="dest"/>. The
    /// literal is matched whole — <c>tru</c>, <c>True</c> and <c>truely</c> all bail — so the
    /// validated reader keeps every diagnostic about a malformed boolean.
    /// </summary>
    /// <returns>Element count, or 0 to fall back to the validated reader.</returns>
    internal static int TryReadBoolArray(
        ReadOnlySpan<byte> data,
        ref int position,
        bool isSequence,
        scoped Span<bool> dest
    )
    {
        if (isSequence)
            return 0;
        var p = position;
        var d = data;
        var len = d.Length;
        if (p >= len)
            return 0;

        int count = 0;
        while (p < len && count < dest.Length)
        {
            byte b = d[p];
            if (b <= 32)
            {
                p = SimdHelpers.SkipWhitespace(d, p);
                continue;
            }
            if (b == (byte)']')
            {
                p++;
                break;
            }
            if (b == (byte)',')
            {
                p++;
                continue;
            }
            if (b == (byte)'t')
            {
                if (p + 3 >= len || d[p + 1] != 'r' || d[p + 2] != 'u' || d[p + 3] != 'e')
                    return 0;
                p += 4;
                dest[count++] = true;
            }
            else if (b == (byte)'f')
            {
                if (
                    p + 4 >= len
                    || d[p + 1] != 'a'
                    || d[p + 2] != 'l'
                    || d[p + 3] != 's'
                    || d[p + 4] != 'e'
                )
                    return 0;
                p += 5;
                dest[count++] = false;
            }
            else
                return 0;
        }
        position = p;
        return count;
    }
}
