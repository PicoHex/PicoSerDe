namespace PicoSerDe.Core;

/// <summary>
/// The eight-slot retention set that lets a token reader publish <see cref="ReadOnlySpan{T}"/>
/// values over pooled arrays and still give those arrays back to <see cref="ArrayPool{T}.Shared"/>.
/// <para>
/// <b>What it is for.</b> A reader that unescapes, re-composes or re-encodes a token cannot point
/// into the input; it rents an array and publishes a span over it. A span is a pointer and a
/// length — it neither keeps the array alive nor says when the consumer is done with it — so the
/// array cannot be returned at the end of the <c>Read()</c> that produced it. This type is the
/// middle ground: it remembers what was rented, returns what it decides is cold, and returns the
/// rest at <see cref="Dispose"/>.
/// </para>
/// <para>
/// <b>Why fields and not an array or a list.</b> Readers are constructed once per deserialize
/// call, so a heap-backed store here is a per-call allocation in the code path that exists to
/// avoid allocations (<c>702fe05</c> turned <c>byte[]?[8]</c> into eight fields for exactly this;
/// <c>47a0447</c> did it for INI first). Eight fields also keep the type free of indirection: it
/// sits inside the reader's <c>ref struct</c> and every access is a field load.
/// </para>
/// <para>
/// <b>The eight slots, and where the word "ring" overstates them.</b> They are not a ring. Slots
/// 0–6 are filled once, in order, and then never touched; slot 7 is the only rotating one. The
/// ninth and every later <see cref="Track"/> returns the array slot 7 holds and puts the new one
/// there:
/// <code>
/// track #1..#7 -> slots 0..6   (pinned; returned only by Dispose)
/// track #8     -> slot 7       (nothing evicted yet)
/// track #9     -> slot 7       (the old #8 goes back to the pool)
/// </code>
/// So the retention set is "the first seven, pinned for the reader's lifetime, plus the most
/// recent one", and <b>a span over a buffer that has left slot 7 is invalid as soon as the pool
/// re-issues that array.</b> Eight is a heuristic — enough consecutive tokens that no realistic
/// consumer still holds the older ones — not a proof. It has to be a heuristic: an exact answer
/// would need the reader to know the consumer's span lifetime, which a <c>ReadOnlySpan</c>
/// contract cannot express. And the number 8 is inherited from the original <c>byte[]?[8]</c>, not
/// derived: this type is a move, not a redesign.
/// </para>
/// <para>
/// <b>When a buffer is reclaimed.</b> Exactly two places: the ninth and later <see cref="Track"/>
/// evicts slot 7, and <see cref="Dispose"/> evicts all eight. Nothing else — a reader that never
/// disposes keeps up to eight arrays rented for as long as it lives. Holding one too long costs
/// memory; returning one too soon costs a consumer reading an array the pool has re-issued, so
/// the trade is always made in the safe direction.
/// </para>
/// <para>
/// <b>After <see cref="Dispose"/>.</b> All slots are null and the fill counter is zero, so the
/// type is reusable and nothing can be returned twice: each slot is nulled as it is returned and a
/// null slot is skipped. A second <see cref="Dispose"/>, or one on a never-used instance, is a
/// no-op. <b>Reuse is not sound on its own</b>, though: the arrays a caller is still looking at
/// are back in the pool's free lists, so publishing spans across a <see cref="Dispose"/> reads
/// memory the pool may have handed out. <see cref="Dispose"/> is terminal for the arrays it
/// returns; it is not a barrier that makes them safe to keep.
/// </para>
/// <para>
/// <b>Why it is a type at all</b> — the reason this file exists. INI, TOML and YAML each carried a
/// byte-identical copy of this machine (<c>_rb0.._rb7</c>, a fill counter, a rent/track switch, an
/// eight-call <c>Dispose</c>, a per-slot return helper); TOML alone held two copies of the switch
/// in one file. Three copies of a retention policy that must never diverge are three chances to
/// edit it in one format and not the others — the same drift <c>FastArrayReaders</c> had already
/// suffered (<c>long digit</c> against <c>int digit</c>) before it was shared. None of the three
/// call sites was format-specific: the policy is about <see cref="ArrayPool{T}"/>, not about
/// TOML's grammar.
/// </para>
/// <para>
/// <b>A note on <c>PicoJetson</c>, so the next reader does not "unify" this by copying it.</b>
/// JSON's reader abandoned this shape on purpose (<c>6564394</c>, C-05) for three named buffers
/// with a stated ownership rule — current token, previous token, the one being filled — returned
/// when the document ends. That is the better model: bounded at two live arrays, and it says what
/// each array is <i>for</i> instead of counting to eight. It is <b>not</b> what this type is,
/// because adopting it changes <i>when</i> arrays come back — buffer #8 of a nine-token document
/// would become invalid during token #9 instead of staying valid. That is a behavior change with
/// its own before/after per reader, so it is recorded here rather than done.
/// </para>
/// <para>
/// <b>Internal, not public.</b> <c>PicoSerDe.Core</c> already grants <c>InternalsVisibleTo</c> to
/// <c>PicoJetson</c>, <c>PicoToml</c>, <c>PicoYaml</c> and <c>PicoIni</c>, so this needs no new
/// grant and does not move <c>api/*.public.txt</c>: it is invisible to consumers and the readers'
/// public surface is unchanged.
/// </para>
/// </summary>
internal struct ByteBufferRing
{
    // Slots 0..6 are pinned by the first seven Track() calls; slot 7 is the rotating one (see the
    // type doc — this is not a ring). Named by position: the only thing these arrays share is that
    // the same reader rented them.
    private byte[]? _slot0,
        _slot1,
        _slot2,
        _slot3,
        _slot4,
        _slot5,
        _slot6,
        _slot7;

    // Track() calls since the last Dispose(). Not a count of live buffers — it is the index into
    // the switch below, so anything past 7 (or negative) means "the rotating slot". Never clamped,
    // because clamping would change the machine rather than move it; the wrap is unreachable in
    // practice: past 2^31 tracks it is negative, which changes nothing, and only after another
    // 2^31 + 7 would it step back through slots 0..6 and overwrite one without returning it.
    private int _next;

    // The array most recently rented or tracked. Write-only: nothing reads it, here or in the three
    // copies this type replaced. Carried over because deleting it is a separate, behavior-neutral
    // cleanup that does not belong inside a move.
    private byte[]? _lastRented;

    /// <summary>Rents an array of at least <paramref name="minimumLength"/> bytes and tracks it.</summary>
    /// <param name="minimumLength">Passed straight to <see cref="ArrayPool{T}.Rent"/>.</param>
    /// <returns>
    /// The rented array, possibly longer than requested. This ring owns it until the pool gets it
    /// back; callers must not return it themselves.
    /// </returns>
    /// <remarks>
    /// Rent and track are one call on purpose: an array that is used but not tracked is a leak
    /// <see cref="Dispose"/> cannot see, and no call site can be trusted to remember half of it.
    /// </remarks>
    public byte[] Rent(int minimumLength)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(minimumLength);
        Track(buffer);
        return buffer;
    }

    /// <summary>Takes ownership of an array the caller already rented, without renting.</summary>
    /// <param name="buffer">The rented array. Not copied, not validated, not cleared.</param>
    /// <remarks>
    /// For callers that rent through something that also does work — grow in place, copy out of a
    /// sequence — and want the ring to hold the result. The ring does not check that
    /// <paramref name="buffer"/> came from <see cref="ArrayPool{T}.Shared"/>; returning a
    /// non-pooled array at <see cref="Dispose"/> is the caller's mistake.
    /// </remarks>
    public void Track(byte[] buffer)
    {
        switch (_next++)
        {
            case 0:
                _slot0 = buffer;
                break;
            case 1:
                _slot1 = buffer;
                break;
            case 2:
                _slot2 = buffer;
                break;
            case 3:
                _slot3 = buffer;
                break;
            case 4:
                _slot4 = buffer;
                break;
            case 5:
                _slot5 = buffer;
                break;
            case 6:
                _slot6 = buffer;
                break;
            default:
                // Eighth and later: slot 7 stops being a slot and becomes a one-place rotating
                // window, and the array it held goes back to the pool here.
                Return(ref _slot7);
                _slot7 = buffer;
                break;
        }
        _lastRented = buffer;
    }

    /// <summary>
    /// Returns all eight slots to <see cref="ArrayPool{T}.Shared"/> and resets the type to its
    /// default state.
    /// </summary>
    /// <remarks>
    /// Idempotent — every slot is nulled as it is returned and a null slot is skipped, so a second
    /// call returns nothing twice. See the type doc for what reuse afterwards means: the arrays are
    /// the pool's again, so spans into them are not safe to hold.
    /// </remarks>
    public void Dispose()
    {
        Return(ref _slot0);
        Return(ref _slot1);
        Return(ref _slot2);
        Return(ref _slot3);
        Return(ref _slot4);
        Return(ref _slot5);
        Return(ref _slot6);
        Return(ref _slot7);
        _next = 0;
        _lastRented = null;
    }

    /// <summary>Returns one slot if it holds an array, then nulls it.</summary>
    /// <param name="slot">The slot to return, nulled so a second pass cannot return it twice.</param>
    /// <remarks>
    /// Private on purpose: this is the ring's pool-return, not a reader operation. The three copies
    /// this replaced each had a <c>private static ReturnBuf(ref byte[]?)</c> with this body, and no
    /// reader ever returned an individual buffer from outside. Exposing it would hand callers the
    /// one move that breaks the policy — returning an array slot 7 still has to return, or one a
    /// consumer is still reading. Eviction is the ring's decision.
    /// </remarks>
    private static void Return(ref byte[]? slot)
    {
        if (slot is not null)
        {
            ArrayPool<byte>.Shared.Return(slot);
            slot = null;
        }
    }
}
