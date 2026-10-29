namespace PicoMsgPack.Tests;

// ── Models ───────────────────────────────────────────────────────────────────────────────────

/// <summary>A <c>List&lt;int&gt;</c> member followed by a scalar, so a mis-sized bulk run shows up
/// as a wrong <see cref="Tail"/> instead of passing silently.</summary>
public class IntListBulkWriteMsgPack
{
    [MsgPackKey(0)]
    public List<int> Values { get; set; } = new();

    [MsgPackKey(1)]
    public int Tail { get; set; }
}

/// <summary>The same shape with a null-capable member: the bulk run must sit inside the null guard.</summary>
public class NullableIntListBulkWriteMsgPack
{
    [MsgPackKey(0)]
    public List<int>? Values { get; set; }

    [MsgPackKey(1)]
    public int Tail { get; set; }
}

/// <summary>An <c>int[]</c> member — the other declared type that converts to a span by itself.</summary>
public class IntArrayBulkWriteMsgPack
{
    [MsgPackKey(0)]
    public int[] Values { get; set; } = [];

    [MsgPackKey(1)]
    public int Tail { get; set; }
}

/// <summary>
/// A member declared as the <c>IList&lt;int&gt;</c> interface: the bulk form applies when the runtime
/// object is a <c>List&lt;int&gt;</c> and must decline (falling back to the element loop) otherwise.
/// </summary>
public class IListBulkWriteMsgPack
{
    [MsgPackKey(0)]
    public IList<int> Values { get; set; } = new List<int>();

    [MsgPackKey(1)]
    public int Tail { get; set; }
}

/// <summary>
/// A member declared as the <c>ICollection&lt;int&gt;</c> interface - the same story as
/// <see cref="IListBulkWriteMsgPack"/>: the declared type does not promise a <c>List&lt;int&gt;</c>.
/// </summary>
public class ICollectionBulkWriteMsgPack
{
    [MsgPackKey(0)]
    public ICollection<int> Values { get; set; } = new List<int>();

    [MsgPackKey(1)]
    public int Tail { get; set; }
}

/// <summary><c>int?</c> elements have no span to hand over: the element loop must stay in charge.</summary>
public class NullableElementListBulkWriteMsgPack
{
    [MsgPackKey(0)]
    public List<int?> Values { get; set; } = new();

    [MsgPackKey(1)]
    public int Tail { get; set; }
}

/// <summary>A converter takes the whole member over — the bulk emit site must not be reached.</summary>
public class ConvertedIntListBulkWriteMsgPack
{
    [MsgPackKey(0)]
    [MsgPackConverter(typeof(ListIntMarkerConverter))]
    public List<int> Values { get; set; } = new();

    [MsgPackKey(1)]
    public int Tail { get; set; }
}

/// <summary>Writes the list as one fixstr marker, so "the converter ran" is visible on the wire.</summary>
public class ListIntMarkerConverter : IMsgPackConverter<List<int>>
{
    public void Write(IBufferWriter<byte> writer, List<int> value)
    {
        writer.Write(System.Text.Encoding.UTF8.GetBytes($"CVT:{value.Count}"));
    }

    public List<int> Read(ref MsgPackReader reader) =>
        Enumerable
            .Range(
                0,
                int.Parse(System.Text.Encoding.UTF8.GetString(reader.GetStringRaw()).Substring(4))
            )
            .ToList();
}

/// <summary>An <c>IList&lt;int&gt;</c> that is deliberately <b>not</b> a <c>List&lt;int&gt;</c>.</summary>
public sealed class BareIntList : IList<int>
{
    private readonly List<int> _inner;

    public BareIntList(IEnumerable<int> values) => _inner = new List<int>(values);

    public int this[int index]
    {
        get => _inner[index];
        set => _inner[index] = value;
    }

    public int Count => _inner.Count;
    public bool IsReadOnly => false;

    public void Add(int item) => _inner.Add(item);

    public void Clear() => _inner.Clear();

    public bool Contains(int item) => _inner.Contains(item);

    public void CopyTo(int[] array, int arrayIndex) => _inner.CopyTo(array, arrayIndex);

    public IEnumerator<int> GetEnumerator() => _inner.GetEnumerator();

    public int IndexOf(int item) => _inner.IndexOf(item);

    public void Insert(int index, int item) => _inner.Insert(index, item);

    public bool Remove(int item) => _inner.Remove(item);

    public void RemoveAt(int index) => _inner.RemoveAt(index);

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
        _inner.GetEnumerator();
}

/// <summary>
/// The bulk int32 write (<c>MsgPackWriter.WriteInt32Array</c>) — the shape a member whose <b>declared</b>
/// type is <c>List&lt;int&gt;</c> / <c>int[]</c> now takes, and the shape every other declared type
/// keeps:
/// <code>
/// mw.WriteStartArray(value.Values.Count);
/// mw.WriteInt32Array(CollectionsMarshal.AsSpan(value.Values));   // List&lt;int&gt;
/// mw.WriteInt32Array(value.Values);                              // int[]
/// mw.WriteEndArray();
/// // IList&lt;int&gt; / ICollection&lt;int&gt; / … : only the element loop, no probe, no bulk arm
/// foreach (var __i in value.Values) { mw.WriteInt32(__i); }
/// </code>
/// <para>
/// <b>What these tests are guarding.</b> The fast path is allowed to exist only when it is
/// indistinguishable on the wire: every case below compares the generated serializer's bytes against
/// <see cref="ReferencePerElementShape"/> — the pre-change loop, written out by hand over the same
/// <see cref="MsgPackWriter"/> — byte for byte, not against a remembered expectation. A single
/// difference in the encoding chosen for any value, in the order of the checks, or in the header
/// fails the comparison.
/// </para>
/// <para>
/// <b>How "the fast path really ran" is proven.</b> <see cref="WriteInt32Array"/> reserves a chunk
/// once for a whole run of fixints, while the per-element loop asks <c>Ensure</c> once per element.
/// <see cref="CountingBufferWriter"/> counts the <c>GetSpan</c> calls, so the two shapes are
/// distinguishable by the number of span fetches alone — the tests named <c>…_TakesTheBulkPath</c>
/// assert on that count; the ones named <c>…_FallsBack…</c> assert the loop's count.
/// </para>
/// </summary>
public class IntListBulkWriteTests
{
    // ── the fast path itself ──

    /// <summary>The case the benchmark row is made of: 100 positive fixints.</summary>
    [Test]
    public async Task Fixints_WireIsIdentical_AndTheBulkPathRan()
    {
        var values = Enumerable.Range(0, 100).ToList();
        var model = new IntListBulkWriteMsgPack { Values = values, Tail = 4242 };

        var payload = MsgPackSerializer.SerializeToUtf8Bytes(model);
        var reference = ReferencePerElementShape(values, 4242);

        await Assert.That(payload).IsEquivalentTo(reference);
        await Assert
            .That(MsgPackSerializer.Deserialize<IntListBulkWriteMsgPack>(payload)!.Values)
            .IsEquivalentTo(values);
    }

    /// <summary>
    /// <b>Witness that the single call ran.</b> <c>Ensure</c> asks the buffer for at least 256 bytes,
    /// so a short list fits in one fetch either way and the fetch count cannot tell the two shapes
    /// apart. A run long enough to cross that boundary can: the per-element loop refetches every 256
    /// bytes, while <c>WriteInt32Array</c> reserves up to <c>MaxInt32Run</c> of them at a time.
    /// </summary>
    [Test]
    public async Task LongFixintRun_TakesTheBulkPath_FewerSpanFetches()
    {
        // Every element is a fixint, so the run never hands an element back to the general path.
        var values = Enumerable.Range(0, 2500).Select(i => i % 100).ToList();

        int bulk = BulkFetches(values);
        int loop = ElementLoopFetches(values);

        await Assert.That(bulk).IsLessThan(loop);
        await Assert.That(BulkReferenceBytes(values)).IsEquivalentTo(ReferenceRootList(values));
    }

    /// <summary>
    /// Every integer encoding the writer can pick, including both fixint ranges and the ones that
    /// leave the fast loop (<c>uint8</c> 0xCC, <c>int16</c> 0xD1, <c>int32</c> 0xD2).
    /// </summary>
    [Test]
    public async Task EveryIntegerEncoding_WireIsIdentical()
    {
        int[] values =
        [
            0,
            127, // positive fixint
            -1,
            -32, // negative fixint
            -33, // just outside the negative fixint range
            128,
            255,
            256,
            -32768,
            32767,
            -32769,
            65536,
            65535,
            -2147483648,
            2147483647,
            1000000,
            0,
            0,
        ];
        var payload = MsgPackSerializer.SerializeToUtf8Bytes(
            new IntListBulkWriteMsgPack { Values = values.ToList(), Tail = -7 }
        );

        await Assert.That(payload).IsEquivalentTo(ReferencePerElementShape(values.ToList(), -7));
        await Assert
            .That(MsgPackSerializer.Deserialize<IntListBulkWriteMsgPack>(payload)!.Values)
            .IsEquivalentTo(values);
    }

    /// <summary>An empty list is a header only: no reservation, no run.</summary>
    [Test]
    public async Task EmptyList_WireIsIdentical()
    {
        var payload = MsgPackSerializer.SerializeToUtf8Bytes(
            new IntListBulkWriteMsgPack { Values = [], Tail = 5 }
        );

        await Assert.That(payload).IsEquivalentTo(ReferencePerElementShape([], 5));
        await Assert.That(payload.Length).IsEqualTo(5);
    }

    /// <summary>A null list still writes <c>nil</c>, and does it in the same place as before.</summary>
    [Test]
    public async Task NullList_WritesNil()
    {
        var payload = MsgPackSerializer.SerializeToUtf8Bytes(
            new NullableIntListBulkWriteMsgPack { Values = null, Tail = 5 }
        );

        await Assert.That(payload).IsEquivalentTo(new byte[] { 0x82, 0x00, 0xC0, 0x01, 0x05 });
        await Assert
            .That(MsgPackSerializer.Deserialize<NullableIntListBulkWriteMsgPack>(payload)!.Values)
            .IsNull();
    }

    /// <summary>An <c>int[]</c> member goes through the same single call.</summary>
    [Test]
    public async Task IntArrayMember_WireIsIdentical()
    {
        int[] values = [0, 1, -1, 300, int.MaxValue];
        var payload = MsgPackSerializer.SerializeToUtf8Bytes(
            new IntArrayBulkWriteMsgPack { Values = values, Tail = 3 }
        );

        await Assert.That(payload).IsEquivalentTo(ReferencePerElementShape(values, 3));
        await Assert
            .That(MsgPackSerializer.Deserialize<IntArrayBulkWriteMsgPack>(payload)!.Values)
            .IsEquivalentTo(values);
    }

    /// <summary>
    /// The root-level <c>List&lt;int&gt;</c> site (a different generated file) takes the same path.
    /// </summary>
    [Test]
    public async Task RootList_WireIsIdentical()
    {
        var values = Enumerable.Range(0, 300).Select(i => i * 7 - 350).ToList();
        var payload = MsgPackSerializer.SerializeToUtf8Bytes(values);

        var reference = ReferenceRootList(values);

        await Assert.That(payload).IsEquivalentTo(reference);
        await Assert.That(MsgPackSerializer.Deserialize<List<int>>(payload)).IsEquivalentTo(values);
    }

    /// <summary>A run longer than one reservation (chunk boundary) still produces the same bytes.</summary>
    [Test]
    public async Task LongRunAcrossTheChunkBoundary_WireIsIdentical()
    {
        var values = Enumerable.Range(0, 2500).Select(i => i - 1250).ToList();
        var payload = MsgPackSerializer.SerializeToUtf8Bytes(
            new IntListBulkWriteMsgPack { Values = values, Tail = 1 }
        );

        await Assert.That(payload).IsEquivalentTo(ReferencePerElementShape(values, 1));
        await Assert
            .That(MsgPackSerializer.Deserialize<IntListBulkWriteMsgPack>(payload)!.Values)
            .IsEquivalentTo(values);
    }

    /// <summary>An <c>IList&lt;int&gt;</c> holding a <c>List&lt;int&gt;</c> takes the single call.</summary>
    [Test]
    public async Task IListBackedByAList_WireIsIdentical()
    {
        var values = Enumerable.Range(0, 100).ToList();
        var model = new IListBulkWriteMsgPack { Values = values, Tail = 9 };

        var payload = MsgPackSerializer.SerializeToUtf8Bytes(model);

        await Assert.That(payload).IsEquivalentTo(ReferencePerElementShape(values, 9));
    }

    // ── the cases the fast path must decline (the per-element loop, unchanged) ──

    /// <summary>
    /// An <c>IList&lt;int&gt;</c> that is not a <c>List&lt;int&gt;</c> and not an array: the bulk form
    /// has no span to hand over, so the element loop runs — and prints the same bytes.
    /// </summary>
    [Test]
    public async Task IListBackedBySomethingElse_FallsBackToTheElementLoop()
    {
        var values = Enumerable.Range(0, 100).ToList();
        var model = new IListBulkWriteMsgPack { Values = new BareIntList(values), Tail = 9 };

        var payload = MsgPackSerializer.SerializeToUtf8Bytes(model);

        await Assert.That(payload).IsEquivalentTo(ReferencePerElementShape(values, 9));
        await Assert
            .That(MsgPackSerializer.Deserialize<IListBulkWriteMsgPack>(payload)!.Values)
            .IsEquivalentTo(values);
    }

    // ── the gate is the DECLARED type of the target, not what the object happens to be ──

    /// <summary>
    /// The control for the two tests below: a member <b>declared</b> <c>List&lt;int&gt;</c> takes the
    /// single call, and the number of span fetches is the bulk shape's count - not the loop's.
    /// The declared type is the only thing that can make this call, so it is also the thing the
    /// emitter has to consult.
    /// </summary>
    [Test]
    public async Task DeclaredListMember_TakesTheBulkPath()
    {
        var values = Enumerable.Range(0, 2500).Select(i => i % 100).ToList();
        var model = new IntListBulkWriteMsgPack { Values = values, Tail = 4242 };

        int actual = ModelFetches(model);
        int bulk = MemberBulkFetches(values, 4242);
        int loop = MemberLoopFetches(values, 4242);

        // Sanity: the two hand-written shapes must be distinguishable, or this test proves nothing.
        await Assert.That(bulk).IsLessThan(loop);
        await Assert.That(actual).IsEqualTo(bulk);
    }

    /// <summary>
    /// <b>An <c>IList&lt;int&gt;</c> member keeps the element loop</b>, even when the object behind it
    /// is a <c>List&lt;int&gt;</c> that a runtime type test would happily accept.
    /// <para>
    /// The gate has to be the target expression's <i>declared</i> type: the bulk write asks for a
    /// span, and only <c>List&lt;int&gt;</c> / <c>int[]</c> can be shown to have one without a runtime
    /// check. A member declared as the interface therefore takes the per-element loop - the shape
    /// it had before the bulk path existed - and no probe of the object's runtime type is emitted
    /// for it.
    /// </para>
    /// </summary>
    [Test]
    public async Task InterfaceDeclaredIListMember_KeepsTheElementLoop()
    {
        var values = Enumerable.Range(0, 2500).Select(i => i % 100).ToList();
        var model = new IListBulkWriteMsgPack { Values = values, Tail = 4242 };

        int actual = ModelFetches(model);

        await Assert.That(actual).IsEqualTo(MemberLoopFetches(values, 4242));
        await Assert.That(actual).IsGreaterThan(MemberBulkFetches(values, 4242));

        // …and it is still the same wire, byte for byte.
        await Assert
            .That(MsgPackSerializer.SerializeToUtf8Bytes(model))
            .IsEquivalentTo(ReferencePerElementShape(values, 4242));
    }

    /// <summary>The same gate for a member declared as <c>ICollection&lt;int&gt;</c>.</summary>
    [Test]
    public async Task InterfaceDeclaredICollectionMember_KeepsTheElementLoop()
    {
        var values = Enumerable.Range(0, 2500).Select(i => i % 100).ToList();
        var model = new ICollectionBulkWriteMsgPack { Values = values, Tail = 4242 };

        int actual = ModelFetches(model);

        await Assert.That(actual).IsEqualTo(MemberLoopFetches(values, 4242));
        await Assert
            .That(MsgPackSerializer.SerializeToUtf8Bytes(model))
            .IsEquivalentTo(ReferencePerElementShape(values, 4242));
    }

    /// <summary><c>List&lt;int?&gt;</c>: no span, and the nulls must still be written as <c>nil</c>.</summary>
    [Test]
    public async Task NullableElements_KeepTheElementLoop()
    {
        List<int?> values = [1, null, -1, null, 300];
        var model = new NullableElementListBulkWriteMsgPack { Values = values, Tail = 2 };

        var payload = MsgPackSerializer.SerializeToUtf8Bytes(model);

        // 0x82 fixmap(2) · 0x00 key · 0x95 fixarray(5) · items · 0x01 key · 0x02 tail
        await Assert
            .That(payload)
            .IsEquivalentTo(
                new byte[]
                {
                    0x82,
                    0x00,
                    0x95,
                    0x01,
                    0xC0,
                    0xFF,
                    0xC0,
                    0xD1,
                    0x01,
                    0x2C,
                    0x01,
                    0x02,
                }
            );
        await Assert
            .That(
                MsgPackSerializer.Deserialize<NullableElementListBulkWriteMsgPack>(payload)!.Values
            )
            .IsEquivalentTo(values);
    }

    /// <summary>A converter owns the member: it writes, the bulk site never runs.</summary>
    [Test]
    public async Task ConverterOwnedList_KeepsTheConverter()
    {
        var model = new ConvertedIntListBulkWriteMsgPack
        {
            Values = Enumerable.Range(0, 100).ToList(),
            Tail = 4,
        };

        var payload = MsgPackSerializer.SerializeToUtf8Bytes(model);

        // 0x82 fixmap(2) · 0x00 key · a7 "CVT:100" (the converter's fixstr) · 0x01 key · 0x04
        await Assert
            .That(payload)
            .IsEquivalentTo(
                new byte[]
                {
                    0x82,
                    0x00,
                    0xA7,
                    (byte)'C',
                    (byte)'V',
                    (byte)'T',
                    (byte)':',
                    (byte)'1',
                    (byte)'0',
                    (byte)'0',
                    0x01,
                    0x04,
                }
            );
    }

    // ── the writer entry point on its own ──

    /// <summary>
    /// <c>WriteInt32Array</c> against a buffer that hands out exactly the requested size, so every
    /// reservation boundary is also a flush boundary: the bytes must still be the loop's bytes and
    /// <c>BytesWritten</c> must still be exact.
    /// </summary>
    [Test]
    public async Task WriteInt32Array_MatchesThePerElementLoop_AcrossFlushes()
    {
        var values = Enumerable
            .Range(0, 5000)
            .Select(i => i % 7 == 0 ? i * 1000 : i - 2500)
            .ToArray();

        var (bulkBytes, bulkCount, bulkWritten) = WriteWithExactBuffer(values, bulk: true);
        var (loopBytes, loopCount, loopWritten) = WriteWithExactBuffer(values, bulk: false);

        await Assert.That(bulkBytes).IsEquivalentTo(loopBytes);
        await Assert.That(bulkCount).IsEqualTo(loopCount);
        await Assert.That(bulkWritten).IsEqualTo(loopWritten);
    }

    /// <summary>An empty run writes nothing and moves no counter.</summary>
    [Test]
    public async Task WriteInt32Array_EmptyRun_IsANoOp()
    {
        var (bytes, count, written) = WriteWithExactBuffer([], bulk: true);

        await Assert.That(count).IsEqualTo(0);
        await Assert.That(bytes).IsEmpty();
        await Assert.That(written).IsEqualTo(0L);
    }

    /// <summary>
    /// Writes <paramref name="values"/> into a buffer that hands out exactly the requested size, so
    /// every reservation is also a flush. Returns the bytes, the buffer's written count and the
    /// writer's own <c>BytesWritten</c>.
    /// </summary>
    static (byte[] Bytes, int WrittenCount, long BytesWritten) WriteWithExactBuffer(
        int[] values,
        bool bulk
    )
    {
        var buffer = new ExactSpanBufferWriter();
        var writer = new MsgPackWriter(buffer);
        if (bulk)
        {
            writer.WriteInt32Array(values);
        }
        else
        {
            foreach (var v in values)
                writer.WriteInt32(v);
        }
        writer.FlushToBuffer();
        return (buffer.WrittenSpan.ToArray(), buffer.WrittenCount, writer.BytesWritten);
    }

    // ── which shapes the generated code actually takes the bulk path for ──

    /// <summary>
    /// The checked-in generated baseline is the honest witness of what the emitter produces: one
    /// bulk call for a member declared <c>List&lt;int&gt;</c> (S1), and the per-element loop left
    /// alone for the shapes that have no span to hand over — a nullable element
    /// (<c>List&lt;int?&gt;</c> in S5), a nested sequence (<c>List&lt;List&lt;int&gt;&gt;</c> in S3) and a
    /// non-int32 element.
    /// <para>
    /// <b>No runtime type probe either.</b> The write side used to emit
    /// <c>if (TryGetInt32Span(x, out var span)) { … } else { loop }</c>, which decided the path
    /// from the object instead of from the declaration and left a branch in every generated file
    /// that a <c>List&lt;int&gt;</c> can never take. S1 now calls the writer directly, so the
    /// baseline must not mention that helper at all.
    /// </para>
    /// </summary>
    [Test]
    public async Task GeneratedBaseline_TakesTheBulkWrite_OnlyForFlatInt32Sequences()
    {
        string Flat(string shape) =>
            File.ReadAllText(
                Path.Combine(
                    GeneratedBaselineDirectory(),
                    $"FormatCapabilities_Probe_{shape}Msgpack_"
                        + (
                            shape switch
                            {
                                "S1" => "6798898a47374dbe",
                                "S3" => "a0203c51c4935edc",
                                "S5" => "be0741cf05408282",
                                _ => throw new ArgumentOutOfRangeException(nameof(shape)),
                            }
                        )
                        + "_MsgPackSerializer.g.cs"
                )
            );

        var flat = Flat("S1");
        await Assert
            .That(flat)
            .Contains(
                "mw.WriteInt32Array(global::System.Runtime.InteropServices.CollectionsMarshal.AsSpan(value.V));"
            );
        // The declared type is the whole gate: nothing probes the object at runtime.
        await Assert.That(flat).DoesNotContain("TryGetInt32Span");

        foreach (var shape in new[] { "S3", "S5" })
        {
            var other = Flat(shape);
            await Assert.That(other).DoesNotContain("WriteInt32Array");
            await Assert.That(other).DoesNotContain("TryGetInt32Span");
        }
    }

    static string GeneratedBaselineDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PicoSerDe.slnx")))
            dir = dir.Parent;
        var root = dir?.FullName ?? throw new InvalidOperationException("repo root not found");
        return Path.Combine(
            root,
            "tests",
            "FormatCapabilities.Probe",
            "generated-baseline",
            "PicoMsgPack.Gen",
            "PicoMsgPack.Gen.MsgPackSerializerGenerator"
        );
    }

    // ── references and instruments ──

    /// <summary>The pre-change generated body for <c>{ List&lt;int&gt; Values; int Tail; }</c>, by hand.</summary>
    static byte[] ReferencePerElementShape(IReadOnlyList<int> values, int tail)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var mw = new MsgPackWriter(buffer);
        mw.WriteStartObject(2);
        mw.WriteInt32(0);
        mw.WriteStartArray(values.Count);
        foreach (var v in values)
            mw.WriteInt32(v);
        mw.WriteEndArray();
        mw.WriteInt32(1);
        mw.WriteInt32(tail);
        mw.WriteEndObject();
        _ = mw.Buffer;
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>The pre-change root-level <c>List&lt;int&gt;</c> serializer, by hand.</summary>
    static byte[] ReferenceRootList(IReadOnlyList<int> values)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var mw = new MsgPackWriter(buffer);
        mw.WriteStartArray(values.Count);
        foreach (var v in values)
            mw.WriteInt32(v);
        mw.WriteEndArray();
        _ = mw.Buffer;
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Span fetches the generated bulk shape needs for this list.</summary>
    static int BulkFetches(List<int> values) =>
        Fetches(w =>
        {
            w.WriteStartArray(values.Count);
            w.WriteInt32Array(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(values));
            w.WriteEndArray();
        });

    /// <summary>The root-list bytes the bulk entry point itself produces for this list.</summary>
    static byte[] BulkReferenceBytes(List<int> values)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var mw = new MsgPackWriter(buffer);
        mw.WriteStartArray(values.Count);
        mw.WriteInt32Array(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(values));
        mw.WriteEndArray();
        _ = mw.Buffer;
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Span fetches the generated serializer for <c>{ List&lt;int&gt; Values; int Tail; }</c> makes.</summary>
    static int ModelFetches<T>(T model)
    {
        var buffer = new CountingBufferWriter();
        MsgPackSerializer.Serialize(buffer, model);
        return buffer.SpanFetches;
    }

    /// <summary>The bulk member shape, written out by hand (one call, one reservation per chunk).</summary>
    static int MemberBulkFetches(IReadOnlyList<int> values, int tail) =>
        Fetches(w =>
        {
            w.WriteStartObject(2);
            w.WriteInt32(0);
            w.WriteStartArray(values.Count);
            w.WriteInt32Array(
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan((List<int>)values)
            );
            w.WriteEndArray();
            w.WriteInt32(1);
            w.WriteInt32(tail);
            w.WriteEndObject();
        });

    /// <summary>The per-element member shape, written out by hand - the pre-bulk code.</summary>
    static int MemberLoopFetches(IReadOnlyList<int> values, int tail) =>
        Fetches(w =>
        {
            w.WriteStartObject(2);
            w.WriteInt32(0);
            w.WriteStartArray(values.Count);
            foreach (var v in values)
                w.WriteInt32(v);
            w.WriteEndArray();
            w.WriteInt32(1);
            w.WriteInt32(tail);
            w.WriteEndObject();
        });

    /// <summary>Span fetches the per-element loop needs for the same list.</summary>
    static int ElementLoopFetches(List<int> values) =>
        Fetches(w =>
        {
            w.WriteStartArray(values.Count);
            foreach (var v in values)
                w.WriteInt32(v);
            w.WriteEndArray();
        });

    static int Fetches(Action<MsgPackWriter> body)
    {
        var buffer = new CountingBufferWriter();
        var writer = new MsgPackWriter(buffer);
        body(writer);
        writer.FlushToBuffer();
        return buffer.SpanFetches;
    }

    /// <summary>Counts <c>GetSpan</c> calls; hands out exactly what was asked for.</summary>
    sealed class CountingBufferWriter : IBufferWriter<byte>
    {
        private byte[] _buffer = new byte[64];
        private int _pos;
        private int _handedOut;

        internal int SpanFetches { get; private set; }

        public void Advance(int count) => _pos += count;

        public Memory<byte> GetMemory(int sizeHint = 0) => throw new NotSupportedException();

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            SpanFetches++;
            if (sizeHint < 1)
                sizeHint = 1;
            if (_pos + sizeHint > _buffer.Length)
                Array.Resize(ref _buffer, Math.Max(_pos + sizeHint, _buffer.Length * 2));
            _handedOut = sizeHint;
            return _buffer.AsSpan(_pos, sizeHint);
        }
    }

    /// <summary>
    /// Hands out exactly the requested size and nothing more, so a write either fits or forces the
    /// encoder to commit first. <c>Advance</c> rejects more than was handed out, which turns a
    /// cursor bug into a failure instead of silent corruption.
    /// </summary>
    sealed class ExactSpanBufferWriter : IBufferWriter<byte>
    {
        private byte[] _buffer = new byte[32];
        private int _pos;
        private int _handedOut;

        internal int WrittenCount => _pos;

        internal ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _pos);

        public void Advance(int count)
        {
            if (count < 0 || count > _handedOut)
                throw new InvalidOperationException(
                    $"Advance({count}) with only {_handedOut} bytes handed out"
                );
            _pos += count;
            _handedOut = 0;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            GetSpan(sizeHint);
            return _buffer.AsMemory(_pos, _handedOut);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            if (sizeHint < 1)
                sizeHint = 1;
            if (_pos + sizeHint > _buffer.Length)
                Array.Resize(ref _buffer, Math.Max(_pos + sizeHint, _buffer.Length * 2));
            _handedOut = sizeHint;
            return _buffer.AsSpan(_pos, sizeHint);
        }
    }
}
