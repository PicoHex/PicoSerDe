namespace PicoSerDe.Core;

/// <summary>
/// Cross-format token-stream contract implemented by all format readers
/// (PicoJetson.JsonReader, PicoIni.IniReader, ...). All readers are ref
/// structs; generic consumers must constrain with `allows ref struct` and
/// never box a reader into this interface.
/// </summary>
public interface ITokenReader
{
    /// <summary>Advances to the next token. Returns false at end of input or
    /// (in streaming mode) when a chunk boundary requires more data.</summary>
    bool Read();

    /// <summary>The token returned by the most recent <see cref="Read"/>.</summary>
    TokenType TokenType { get; }

    bool TryGetInt32(out int value);
    bool TryGetInt64(out long value);
    bool TryGetFloat64(out double value);
    bool TryGetBool(out bool value);
    bool TrySkip();

    /// <summary>True when the last failed <see cref="Read"/> hit a chunk boundary (streaming).</summary>
    bool NeedsMoreData { get; }

    /// <summary>Current nesting depth.</summary>
    int Depth { get; }
}

/// <summary>
/// The fused-read capability of an <see cref="ITokenReader"/>: "classify a token" and "decode its
/// value" happen in <b>one</b> dispatch.
/// <para>
/// <b>Why a separate interface instead of new members on <see cref="ITokenReader"/></b>: a
/// <see langword="ref struct"/> must implement every member of every interface it implements,
/// default interface members included (<c>CS9245</c>). Adding these members to
/// <see cref="ITokenReader"/> would break every ref-struct implementer, in tree and out of tree.
/// Implement this interface to opt in; implementers of <see cref="ITokenReader"/> alone are
/// unaffected.
/// </para>
/// <para>
/// <b>There are no default bodies.</b> A <see langword="ref struct"/> cannot inherit one, so every
/// implementer writes all six members. A format that does not optimize a member MUST implement
/// exactly the reference semantics of spec §3.2 — <see cref="TokenReaderFused"/> — forward to it,
/// do not re-derive the combination.
/// </para>
/// </summary>
public interface IFusedTokenReader : ITokenReader
{
    /// <summary>
    /// True when this reader can report the element count of a collection from the token
    /// <see cref="ITokenReader.Read"/> just produced — i.e. the wire format carries the count in the
    /// collection header (MessagePack does; JSON/TOML/YAML/INI do not).
    /// <para>
    /// A capability declaration, not a hint: when it is <see langword="false"/>,
    /// <see cref="TryGetCollectionCount"/> MUST return <see langword="false"/> for every token,
    /// and consumers must terminate the element loop with <see cref="ITokenReader.Read"/> plus the
    /// end token. A reader answering <see langword="true"/> MUST answer
    /// <see cref="TryGetCollectionCount"/> with <see langword="true"/> right after every
    /// <see cref="TokenType.ArrayStart"/>/<see cref="TokenType.ObjectStart"/> it produced.
    /// </para>
    /// </summary>
    bool HasCountedCollections { get; }

    /// <summary>
    /// Reports, without advancing, how many element iterations the current collection will still
    /// yield before <see cref="ITokenReader.Read"/> produces the matching
    /// <see cref="TokenType.ArrayEnd"/>/<see cref="TokenType.ObjectEnd"/>.
    /// <para>
    /// <b>Contract</b>: a map/object level counts <b>key-value pairs</b>, not tokens, so
    /// <c>for (i = 0; i &lt; count; i++)</c> is exactly one iteration per pair. The number is the same
    /// number the reader uses for its own end-token decision, so a counted loop and the plain
    /// <see cref="ITokenReader.Read"/>-driven loop issue identical token sequences (trailing end
    /// token included).
    /// <b>Never changes observable state</b>: no position move, no
    /// <see cref="ITokenReader.TokenType"/>/<see cref="ITokenReader.Depth"/>/
    /// <see cref="ITokenReader.NeedsMoreData"/> change, no span invalidation — a query in the
    /// <c>TryGetInt32</c> family, not a read. Returns <see langword="false"/> (with
    /// <paramref name="count"/> = 0) when the count is unknown — the format carries none, no
    /// collection token was produced yet, or the reader is in a mode it does not serve; callers then
    /// fall back to the <see cref="ITokenReader.Read"/>-driven loop.
    /// </para>
    /// </summary>
    bool TryGetCollectionCount(out int count);

    /// <summary>
    /// Advances exactly one token and, when that token decodes as an <see cref="int"/>, returns it —
    /// <b>equivalent to</b> <c>Read() &amp;&amp; TryGetInt32(out value)</c>, including every observable
    /// side effect (that literal combination is <see cref="TokenReaderFused.TryReadInt32{T}"/>).
    /// <b>Failure is lossless</b>: the token <see cref="ITokenReader.Read"/> produced stays current,
    /// so a caller seeing <see langword="false"/> can still inspect
    /// <see cref="ITokenReader.TokenType"/> and use the <c>TryGet*</c> family or
    /// <see cref="ITokenReader.TrySkip"/> on it. When <see cref="ITokenReader.Read"/> itself fails,
    /// <see cref="ITokenReader.NeedsMoreData"/> reports a chunk boundary exactly as there.
    /// </summary>
    bool TryReadInt32(out int value);

    /// <summary>See <see cref="TryReadInt32"/>; identical contract for <see cref="long"/>.</summary>
    bool TryReadInt64(out long value);

    /// <summary>See <see cref="TryReadInt32"/>; identical contract for <see cref="double"/>.</summary>
    bool TryReadFloat64(out double value);

    /// <summary>See <see cref="TryReadInt32"/>; identical contract for <see cref="bool"/>.</summary>
    bool TryReadBool(out bool value);
}

/// <summary>
/// The reference semantics of the four fused reads in <see cref="IFusedTokenReader"/>: each method is
/// the literal combination <c>Read() &amp;&amp; TryGetX(out value)</c>, written once so that it cannot
/// drift per format.
/// <para>
/// <b>Who uses it</b>: a reader that does not optimize a fused read forwards to it in one line —
/// <c>=&gt; TokenReaderFused.TryReadInt32(ref this, out value)</c> — so "this format does not optimize"
/// cannot turn into five subtly different definitions. The forwarding member must <b>not</b> be
/// <see langword="readonly"/>: inside a <see langword="readonly"/> member <c>this</c> is a read-only
/// variable and <c>ref this</c> fails with <c>CS1605</c>.
/// </para>
/// <para>
/// <b>Why static methods with a <c>ref</c> parameter, not extension methods</b>: an extension method's
/// receiver cannot be passed by reference to a type parameter that allows ref structs
/// (<c>CS8337</c>/<c>CS9244</c>/<c>CS9300</c>), and the by-value receiver form compiles but advances a
/// <b>copy</b> — the caller's reader never moves. The <c>ref</c> parameter is the point, not a detail.
/// </para>
/// <para>
/// <b>Equivalence is the contract</b>: only the <c>TryGetX</c> call after a successful
/// <see cref="ITokenReader.Read"/> may differ from the hand-written pair, and only by doing fewer
/// internal dispatches. <c>NeedsMoreData</c> is set by <see cref="ITokenReader.Read"/> alone and is
/// never touched here; <see cref="ITokenReader.TokenType"/> is never rolled back on failure; on
/// failure <c>value</c> is 0.
/// </para>
/// </summary>
public static class TokenReaderFused
{
    /// <summary>
    /// Advances one token and reads it as an <see cref="int"/>: exactly
    /// <c>Read() &amp;&amp; TryGetInt32(out value)</c>, including the state <see cref="ITokenReader.Read"/>
    /// materialised. On failure <paramref name="value"/> is 0, the token <see cref="ITokenReader.Read"/>
    /// just produced stays current, and <see cref="ITokenReader.NeedsMoreData"/> is left as
    /// <see cref="ITokenReader.Read"/> reported it.
    /// </summary>
    /// <typeparam name="T">The concrete reader type; a ref struct is allowed.</typeparam>
    /// <param name="reader">The reader to advance — by reference, so a ref struct is not copied.</param>
    /// <param name="value">The decoded value, or 0 when the method returns false.</param>
    /// <returns>True when a token was consumed and decoded as an <see cref="int"/>.</returns>
    public static bool TryReadInt32<T>(ref T reader, out int value)
        where T : ITokenReader, allows ref struct
    {
        if (!reader.Read())
        {
            value = 0;
            return false;
        }

        if (reader.TryGetInt32(out value))
            return true;

        value = 0;
        return false;
    }

    /// <summary>See <see cref="TryReadInt32{T}"/>; identical contract for <see cref="long"/>.</summary>
    /// <typeparam name="T">The concrete reader type; a ref struct is allowed.</typeparam>
    /// <param name="reader">The reader to advance — by reference, so a ref struct is not copied.</param>
    /// <param name="value">The decoded value, or 0 when the method returns false.</param>
    /// <returns>True when a token was consumed and decoded as a <see cref="long"/>.</returns>
    public static bool TryReadInt64<T>(ref T reader, out long value)
        where T : ITokenReader, allows ref struct
    {
        if (!reader.Read())
        {
            value = 0;
            return false;
        }

        if (reader.TryGetInt64(out value))
            return true;

        value = 0;
        return false;
    }

    /// <summary>See <see cref="TryReadInt32{T}"/>; identical contract for <see cref="double"/>.</summary>
    /// <typeparam name="T">The concrete reader type; a ref struct is allowed.</typeparam>
    /// <param name="reader">The reader to advance — by reference, so a ref struct is not copied.</param>
    /// <param name="value">The decoded value, or 0 when the method returns false.</param>
    /// <returns>True when a token was consumed and decoded as a <see cref="double"/>.</returns>
    public static bool TryReadFloat64<T>(ref T reader, out double value)
        where T : ITokenReader, allows ref struct
    {
        if (!reader.Read())
        {
            value = 0;
            return false;
        }

        if (reader.TryGetFloat64(out value))
            return true;

        value = 0;
        return false;
    }

    /// <summary>See <see cref="TryReadInt32{T}"/>; identical contract for <see cref="bool"/>.</summary>
    /// <typeparam name="T">The concrete reader type; a ref struct is allowed.</typeparam>
    /// <param name="reader">The reader to advance — by reference, so a ref struct is not copied.</param>
    /// <param name="value">The decoded value, or false when the method returns false.</param>
    /// <returns>True when a token was consumed and decoded as a <see cref="bool"/>.</returns>
    public static bool TryReadBool<T>(ref T reader, out bool value)
        where T : ITokenReader, allows ref struct
    {
        if (!reader.Read())
        {
            value = false;
            return false;
        }

        if (reader.TryGetBool(out value))
            return true;

        value = false;
        return false;
    }
}
