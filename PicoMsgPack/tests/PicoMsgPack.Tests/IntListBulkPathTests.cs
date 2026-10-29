namespace PicoMsgPack.Tests;

// ── Models ───────────────────────────────────────────────────────────────────────────────────

/// <summary>A single <c>List&lt;int&gt;</c> member followed by a scalar, so a misaligned token walk
/// shows up as a wrong <see cref="Tail"/> instead of passing silently.</summary>
public class IntListBulkMsgPack
{
    [MsgPackKey(0)]
    public List<int> Values { get; set; } = new();

    [MsgPackKey(1)]
    public int Tail { get; set; }
}

/// <summary>The member starts out non-empty: the decoded elements must be <i>appended</i>.</summary>
public class PrefilledIntListBulkMsgPack
{
    [MsgPackKey(0)]
    public List<int> Values { get; set; } = new() { 99 };

    [MsgPackKey(1)]
    public int Tail { get; set; }
}

/// <summary>An <c>int[]</c> member — the same emitter site, a <c>List&lt;int&gt;</c> behind the scenes.</summary>
public class IntArrayBulkMsgPack
{
    [MsgPackKey(0)]
    public int[] Values { get; set; } = [];

    [MsgPackKey(1)]
    public int Tail { get; set; }
}

/// <summary>
/// The bulk int32 list read (<c>__PicoMsgPackBulk.TryReadInt32List</c>) — the shape a counted
/// <c>List&lt;int&gt;</c> loop now takes:
/// <code>
/// if (reader.TryGetCollectionCount(out var __cn)) {
///     if (!__PicoMsgPackBulk.TryReadInt32List(ref reader, obj.Values, __cn)) {
///         for (…) { if (!reader.Read()) break; if (!reader.TryGetInt32(…)) throw …; obj.Values.Add(…); }
///     }
///     reader.Read();
/// }
/// </code>
/// <para>
/// <b>What these tests are guarding.</b> The fast path is allowed to exist only when it is
/// indistinguishable from the per-element loop: same values, same partial list on truncation, same
/// exception type <i>and</i> message, same token alignment afterwards, and no storage reserved from
/// a count the payload cannot honour. Every case below therefore compares the generated path against
/// <see cref="ReferencePerElementShape"/> (the pre-change loop, written out by hand over the same
/// <see cref="MsgPackReader"/>) rather than against a remembered expectation.
/// </para>
/// <para>
/// <b>How "the fast path really ran" is proven.</b> The bulk read writes straight into the list's
/// own storage, so the list's <c>Capacity</c> ends up exactly at the element count; the per-element
/// loop starts from the generator's <c>new List&lt;int&gt;(16)</c> and doubles
/// (16→32→64→128 for 100 elements). A capacity equal to the count is therefore evidence that the
/// single-call path ran, and the tests that assert it are named <c>…_TakesTheBulkPath</c>.
/// </para>
/// </summary>
public class IntListBulkPathTests
{
    // ── the fast path itself ──

    /// <summary>Positive fixints: the case the row is made of.</summary>
    [Test]
    public async Task FixintArray_TakesTheBulkPath_AndMatchesTheReference()
    {
        var values = Enumerable.Range(0, 100).ToList();
        var model = new IntListBulkMsgPack { Values = values, Tail = 4242 };
        var payload = MsgPackSerializer.SerializeToUtf8Bytes(model);

        var generated = MsgPackSerializer.Deserialize<IntListBulkMsgPack>(payload);
        var reference = ReferencePerElementShape(payload);

        await Assert.That(generated).IsNotNull();
        await Assert.That(generated!.Values).IsEquivalentTo(reference.Values);
        await Assert.That(generated.Values).IsEquivalentTo(values);
        await Assert.That(generated.Tail).IsEqualTo(4242);
        // Evidence that the single-call path ran: growth would have left 128 here.
        await Assert.That(generated.Values.Capacity).IsEqualTo(100);
    }

    /// <summary>Negative fixints and the wider int encodings, all inside one array.</summary>
    [Test]
    public async Task MixedIntegerEncodings_TakesTheBulkPath_AndMatchesTheReference()
    {
        // fixmap(2): 0 → fixarray(6) [fixint, negative fixint, uint8, uint16, uint32, int32], 1 → 7
        byte[] payload =
        [
            0x82,
            0x00,
            0x96,
            0x7F, // 127
            0xFF, // -1
            0xCC,
            0xC8, // uint8 200
            0xCD,
            0x01,
            0x2C, // uint16 300
            0xCE,
            0x00,
            0x01,
            0x86,
            0xA0, // uint32 100000
            0xD2,
            0x00,
            0x00,
            0x00,
            0x2A, // int32 42
            0x01,
            0x07, // Tail = 7
        ];

        var generated = MsgPackSerializer.Deserialize<IntListBulkMsgPack>(payload);
        var reference = ReferencePerElementShape(payload);

        await Assert.That(generated).IsNotNull();
        await Assert.That(generated!.Values).IsEquivalentTo(reference.Values);
        await Assert.That(generated.Values).IsEquivalentTo(new[] { 127, -1, 200, 300, 100000, 42 });
        await Assert.That(generated.Tail).IsEqualTo(7);
        await Assert.That(generated.Values.Capacity).IsEqualTo(6);
    }

    /// <summary>array16 header (300 elements) — the count comes from a two-byte header.</summary>
    [Test]
    public async Task Array16Header_TakesTheBulkPath_AndKeepsAlignment()
    {
        var values = Enumerable.Range(0, 300).Select(i => i * 3 - 100).ToList();
        var model = new IntListBulkMsgPack { Values = values, Tail = -1 };
        var payload = MsgPackSerializer.SerializeToUtf8Bytes(model);

        var generated = MsgPackSerializer.Deserialize<IntListBulkMsgPack>(payload);
        var reference = ReferencePerElementShape(payload);

        await Assert.That(generated).IsNotNull();
        await Assert.That(generated!.Values).IsEquivalentTo(reference.Values);
        await Assert.That(generated.Values).IsEquivalentTo(values);
        await Assert.That(generated.Tail).IsEqualTo(-1);
        await Assert.That(generated.Values.Capacity).IsEqualTo(300);
    }

    /// <summary>Root-level <c>List&lt;int&gt;</c> is the other wired site.</summary>
    [Test]
    public async Task RootList_TakesTheBulkPath_AndMatchesTheReference()
    {
        // 100 elements: the generator starts the list at capacity 16, so a capacity of exactly 100
        // is the fast path's signature (growth would stop at 128).
        var values = Enumerable.Range(0, 100).Select(i => i * 7 - 350).ToArray();
        var payload = MsgPackSerializer.SerializeToUtf8Bytes(new List<int>(values));

        var generated = MsgPackSerializer.Deserialize<List<int>>(payload);
        var reference = ReferencePerElementRootList(payload);

        await Assert.That(generated).IsNotNull();
        await Assert.That(generated!).IsEquivalentTo(reference);
        await Assert.That(generated).IsEquivalentTo(values);
        await Assert.That(generated.Capacity).IsEqualTo(values.Length);
    }

    /// <summary>An <c>int[]</c> member reads through the same <c>List&lt;int&gt;</c> path.</summary>
    [Test]
    public async Task IntArrayMember_TakesTheBulkPath_AndRoundTrips()
    {
        int[] values = [0, 1, -1, 300, int.MaxValue];
        var model = new IntArrayBulkMsgPack { Values = values, Tail = 3 };
        var payload = MsgPackSerializer.SerializeToUtf8Bytes(model);

        var generated = MsgPackSerializer.Deserialize<IntArrayBulkMsgPack>(payload);

        await Assert.That(generated).IsNotNull();
        await Assert.That(generated!.Values).IsEquivalentTo(values);
        await Assert.That(generated.Tail).IsEqualTo(3);
    }

    /// <summary>A member that already holds elements is appended to, exactly as before.</summary>
    [Test]
    public async Task PrefilledMember_AppendsAfterTheExistingElements()
    {
        var payload = MsgPackSerializer.SerializeToUtf8Bytes(
            new IntListBulkMsgPack { Values = [1, 2, 3], Tail = 8 }
        );

        var generated = MsgPackSerializer.Deserialize<PrefilledIntListBulkMsgPack>(payload);
        var reference = ReferencePerElementShapePrefilled(payload);

        await Assert.That(generated).IsNotNull();
        await Assert.That(generated!.Values).IsEquivalentTo(reference.Values);
        await Assert.That(generated.Values).IsEquivalentTo(new[] { 99, 1, 2, 3 });
        await Assert.That(generated.Tail).IsEqualTo(8);
    }

    // ── the cases the fast path must decline ──

    /// <summary>
    /// <c>int64</c> elements are not <c>int32</c>: the bulk read must refuse the whole array (it
    /// consumed nothing) and the per-element loop must decode the values, including the one that
    /// does not fit an <c>int</c> at all.
    /// </summary>
    [Test]
    public async Task Int64Elements_DeclineTheBulkPath_AndKeepTheOldAnswer()
    {
        // fixmap(2): 0 → fixarray(3) [int64 5, int64 -7, int64 2^40], 1 → 9
        byte[] payload =
        [
            0x82,
            0x00,
            0x93,
            0xD3,
            0x00,
            0x00,
            0x00,
            0x00,
            0x00,
            0x00,
            0x00,
            0x05,
            0xD3,
            0xFF,
            0xFF,
            0xFF,
            0xFF,
            0xFF,
            0xFF,
            0xFF,
            0xF9,
            0xD3,
            0x00,
            0x00,
            0x01,
            0x00,
            0x00,
            0x00,
            0x00,
            0x00,
            0x01,
            0x09,
        ];

        var generated = ErrorOf(() => MsgPackSerializer.Deserialize<IntListBulkMsgPack>(payload));
        var reference = ErrorOf(() => ReferencePerElementShape(payload));

        // 2^40 does not fit an int, so the per-element loop raises FormatException — and the bulk
        // path must reach exactly the same verdict, with the same message.
        await Assert.That(generated).IsNotNull();
        await Assert.That(reference).IsNotNull();
        await Assert.That(generated!.GetType()).IsEqualTo(reference!.GetType());
        await Assert.That(generated.Message).IsEqualTo(reference.Message);
    }

    /// <summary>A <c>uint64</c> above <c>long.MaxValue</c> is refused by both shapes the same way.</summary>
    [Test]
    public async Task UInt64Elements_DeclineTheBulkPath_AndKeepTheOldAnswer()
    {
        // fixmap(2): 0 → fixarray(2) [uint64 1, uint64 2^63], 1 → 3
        byte[] payload =
        [
            0x82,
            0x00,
            0x92,
            0xCF,
            0x00,
            0x00,
            0x00,
            0x00,
            0x00,
            0x00,
            0x00,
            0x01,
            0xCF,
            0x80,
            0x00,
            0x00,
            0x00,
            0x00,
            0x00,
            0x00,
            0x00,
            0x01,
            0x03,
        ];

        var generated = ErrorOf(() => MsgPackSerializer.Deserialize<IntListBulkMsgPack>(payload));
        var reference = ErrorOf(() => ReferencePerElementShape(payload));

        await Assert.That(generated).IsNotNull();
        await Assert.That(reference).IsNotNull();
        await Assert.That(generated!.GetType()).IsEqualTo(reference!.GetType());
        await Assert.That(generated.Message).IsEqualTo(reference.Message);
    }

    /// <summary>A string where an int is expected: same exception, same message, same offset.</summary>
    [Test]
    public async Task NonIntegerElement_DeclinesTheBulkPath_AndThrowsLikeTheReference()
    {
        // fixmap(2): 0 → fixarray(2) [42, "abc"], 1 → 1
        byte[] payload = [0x82, 0x00, 0x92, 0x2A, 0xA3, 0x61, 0x62, 0x63, 0x01, 0x01];

        var generated = ErrorOf(() => MsgPackSerializer.Deserialize<IntListBulkMsgPack>(payload));
        var reference = ErrorOf(() => ReferencePerElementShape(payload));

        await Assert.That(generated).IsNotNull();
        await Assert.That(reference).IsNotNull();
        await Assert.That(generated!.GetType()).IsEqualTo(reference!.GetType());
        await Assert.That(generated.Message).IsEqualTo(reference.Message);
    }

    /// <summary>Truncation inside the array: the partial list is the answer, as before.</summary>
    [Test]
    public async Task TruncatedArray_DeclinesTheBulkPath_AndKeepsThePartialList()
    {
        // fixarray(2) holding a single element.
        byte[] payload = [0x92, 0x01];

        var generated = MsgPackSerializer.Deserialize<List<int>>(payload);
        var reference = ReferencePerElementRootList(payload);

        await Assert.That(generated).IsNotNull();
        await Assert.That(generated!).IsEquivalentTo(reference);
        await Assert.That(generated!.Count).IsEqualTo(1);
        await Assert.That(generated[0]).IsEqualTo(1);
    }

    /// <summary>An empty array has nothing to bulk-read; the end token still lands correctly.</summary>
    [Test]
    public async Task EmptyArray_ReturnsAnEmptyList_AndKeepsAlignment()
    {
        // fixmap(2): 0 → fixarray(0), 1 → 5
        byte[] payload = [0x82, 0x00, 0x90, 0x01, 0x05];

        var generated = MsgPackSerializer.Deserialize<IntListBulkMsgPack>(payload);
        var reference = ReferencePerElementShape(payload);

        await Assert.That(generated).IsNotNull();
        await Assert.That(generated!.Values.Count).IsEqualTo(0);
        await Assert.That(generated.Tail).IsEqualTo(5);
        await Assert.That(generated.Tail).IsEqualTo(reference.Tail);
    }

    /// <summary>
    /// A header that claims far more elements than the payload holds must not reserve storage for
    /// them: the shape is refused before anything is allocated, and the answer is the reference's
    /// truncated-container verdict.
    /// </summary>
    [Test]
    public async Task HostileCount_DoesNotPreallocate_AndKeepsTheOldVerdict()
    {
        // fixmap(2): 0 → array16 declaring 65535 with two elements present, 1 → 9
        byte[] payload = [0x82, 0x00, 0xDC, 0xFF, 0xFF, 0x01, 0x02, 0x01, 0x09];

        long before = GC.GetAllocatedBytesForCurrentThread();
        var generated = ErrorOf(() => MsgPackSerializer.Deserialize<IntListBulkMsgPack>(payload));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        var reference = ErrorOf(() => ReferencePerElementShape(payload));

        await Assert.That(generated).IsNotNull();
        await Assert.That(reference).IsNotNull();
        await Assert.That(generated!.GetType()).IsEqualTo(reference!.GetType());
        await Assert.That(generated.Message).IsEqualTo(reference.Message);
        // 65535 elements would be 256 KiB; the whole document is nine bytes.
        await Assert.That(allocated).IsLessThan(64 * 1024);
    }

    /// <summary>The streaming deserializer keeps working across chunk boundaries.</summary>
    [Test]
    public async Task StreamingArm_Unaffected_AcrossChunkBoundaries()
    {
        var values = Enumerable.Range(0, 40).Select(i => i - 20).ToList();
        var model = new IntListBulkMsgPack { Values = values, Tail = 77 };
        var payload = MsgPackSerializer.SerializeToUtf8Bytes(model);

        foreach (var chunkSize in new[] { 1, 2, 3, 7 })
        {
            using var stream = new ChunkedReadStream(payload, chunkSize);
            var back = await MsgPackSerializer.DeserializeFromStreamAsync<IntListBulkMsgPack>(
                stream
            );
            await Assert.That(back).IsNotNull();
            await Assert.That(back!.Values).IsEquivalentTo(values);
            await Assert.That(back.Tail).IsEqualTo(77);
        }
    }

    // ── reference arms: the per-element loop, hand-written over the same reader ──

    static IntListBulkMsgPack ReferencePerElementShape(ReadOnlySpan<byte> payload)
    {
        var model = new IntListBulkMsgPack();
        var reader = new MsgPackReader(payload);
        reader.Read(); // ObjectStart
        while (reader.Read())
        {
            if (reader.TokenType is TokenType.ObjectEnd or TokenType.ArrayEnd)
                break;
            reader.TryGetInt32(out var key);
            reader.Read(); // the member's value token
            switch (key)
            {
                case 0:
                    foreach (var v in ReadIntElements(ref reader))
                        model.Values.Add(v);
                    break;
                case 1:
                    if (!reader.TryGetInt32(out var tail))
                        throw new FormatException(
                            $"Expected an integer at offset {reader.BytesConsumed}"
                        );
                    model.Tail = tail;
                    break;
            }
        }
        if (reader.Depth != 0)
            throw new FormatException("Truncated container: unexpected end of input");
        return model;
    }

    static PrefilledIntListBulkMsgPack ReferencePerElementShapePrefilled(ReadOnlySpan<byte> payload)
    {
        var model = new PrefilledIntListBulkMsgPack();
        var reader = new MsgPackReader(payload);
        reader.Read();
        while (reader.Read())
        {
            if (reader.TokenType is TokenType.ObjectEnd or TokenType.ArrayEnd)
                break;
            reader.TryGetInt32(out var key);
            reader.Read();
            switch (key)
            {
                case 0:
                    foreach (var v in ReadIntElements(ref reader))
                        model.Values.Add(v);
                    break;
                case 1:
                    if (!reader.TryGetInt32(out var tail))
                        throw new FormatException(
                            $"Expected an integer at offset {reader.BytesConsumed}"
                        );
                    model.Tail = tail;
                    break;
            }
        }
        if (reader.Depth != 0)
            throw new FormatException("Truncated container: unexpected end of input");
        return model;
    }

    static List<int> ReferencePerElementRootList(ReadOnlySpan<byte> payload)
    {
        var list = new List<int>();
        var reader = new MsgPackReader(payload);
        reader.Read(); // ArrayStart
        foreach (var v in ReadIntElements(ref reader))
            list.Add(v);
        return list;
    }

    /// <summary>The pre-change element loop: one <c>Read()</c> plus one <c>TryGetInt32()</c> per element.</summary>
    static List<int> ReadIntElements(ref MsgPackReader reader)
    {
        var list = new List<int>();
        if (reader.TokenType != TokenType.ArrayStart)
            return list;
        while (reader.Read() && reader.TokenType != TokenType.ArrayEnd)
        {
            if (!reader.TryGetInt32(out var v))
                throw new FormatException(
                    $"Expected an integer element at offset {reader.BytesConsumed}"
                );
            list.Add(v);
        }
        return list;
    }

    static Exception? ErrorOf(Func<object?> action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>Hands out at most <paramref name="chunkSize"/> bytes per <c>ReadAsync</c>.</summary>
    sealed class ChunkedReadStream(byte[] data, int chunkSize) : Stream
    {
        int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position
        {
            get => _offset;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            var size = Math.Min(chunkSize, Math.Min(buffer.Length, data.Length - _offset));
            if (size <= 0)
                return ValueTask.FromResult(0);
            data.AsMemory(_offset, size).CopyTo(buffer);
            _offset += size;
            return ValueTask.FromResult(size);
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
