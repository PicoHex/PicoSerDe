namespace PicoJetson.Tests;

public class ReaderBufferOwnershipTests
{
    [Test]
    public async Task PeakLiveBuffers_StaysBounded()
    {
        // 12 个字符串属性 = 24 次 Rent；8 槽轮转模型下 _bufCount 单调增长，
        // 修复后（只保留 span 源 + 当前 token）峰值必须 <= 2。
        var sb = new StringBuilder("{");
        for (var i = 1; i <= 12; i++)
            sb.Append(i == 1 ? "" : ",").Append($"\"P{i}\":\"v{i}\"");
        sb.Append('}');
        var reader = new JsonReader(
            new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(sb.ToString())),
            maxDepth: 256,
            isFinalBlock: true
        );
        while (reader.Read()) { }
        // Reads past the end of the document: every retained span is released.
        var leaked = reader.LeakedBufferCount;
        var peak = reader.PeakTrackedBufferCount;
        reader.Dispose();
        var tracked = reader.TrackedBufferCount;
        var returns = reader.TotalPoolReturns;
        var trackedTotal = reader.TotalTrackedBuffers;

        await Assert.That(leaked).IsZero();
        await Assert.That(peak).IsLessThanOrEqualTo(2);
        await Assert.That(tracked).IsZero();
        await Assert.That(returns).IsEqualTo(trackedTotal);
    }

    [Test]
    public async Task PropertyNameSpan_SurvivesManyReads()
    {
        var json = Encoding.UTF8.GetBytes(
            "{\"P1\":\"v1\",\"P2\":\"v2\",\"P3\":\"v3\",\"P4\":\"v4\",\"P5\":\"v5\",\"P6\":\"v6\"}"
        );
        var reader = new JsonReader(
            new ReadOnlySequence<byte>(json),
            maxDepth: 256,
            isFinalBlock: true
        );
        var names = new List<string>();
        var ok = true;
        try
        {
            ok = reader.Read();
            for (var i = 1; ok && i <= 6; i++)
            {
                if (!(ok = reader.Read()))
                    break;
                var name = reader.GetStringRaw();
                if (!(ok = reader.Read()))
                    break;
                var probe = ArrayPool<byte>.Shared.Rent(256);
                probe.AsSpan().Fill((byte)'X');
                ArrayPool<byte>.Shared.Return(probe);
                names.Add(Encoding.UTF8.GetString(name));
            }
        }
        finally
        {
            reader.Dispose();
        }

        await Assert.That(ok).IsTrue();
        await Assert.That(names.Count).IsEqualTo(6);
        for (var i = 1; i <= names.Count; i++)
            await Assert.That(names[i - 1]).IsEqualTo($"P{i}");
    }

    [Test]
    public async Task ValueSpan_IsEmptyAfterEndOfDocument_Sequence()
    {
        // Sequence mode rents a pooled buffer for every string token. Once Read()
        // reports the end of the document, those buffers go back to the pool, so a
        // retained span would alias a recycled array (use-after-return). The
        // contract must be an explicit empty span, not stale bytes.
        var reader = new JsonReader(
            new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes("{\"P1\":\"v1\"}")),
            maxDepth: 256,
            isFinalBlock: true
        );
        var ok = true;
        while (ok)
            ok = reader.Read();

        // Read() returned false (EOF) — every tracked buffer was handed back.
        var valueSpanLength = reader.ValueSpan.Length;
        var stringRawLength = reader.GetStringRaw().Length;
        var leaked = reader.LeakedBufferCount;
        var tracked = reader.TrackedBufferCount;
        reader.Dispose();

        await Assert.That(valueSpanLength).IsZero();
        await Assert.That(stringRawLength).IsZero();
        await Assert.That(leaked).IsZero();
        await Assert.That(tracked).IsZero();
    }

    [Test]
    public async Task GetStringRaw_DoesNotAliasRecycledBuffer_AfterEndOfDocument()
    {
        // The dangling span from the previous test is only *observable* as garbage
        // when the pool hands the returned array to the next renter. Force that
        // reuse with a probe rent, then read the span again.
        var reader = new JsonReader(
            new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes("{\"P1\":\"v1\"}")),
            maxDepth: 256,
            isFinalBlock: true
        );
        var ok = true;
        while (ok)
            ok = reader.Read();

        var probe = ArrayPool<byte>.Shared.Rent(256);
        probe.AsSpan().Fill((byte)'Z');
        ArrayPool<byte>.Shared.Return(probe);

        var reused = reader.GetStringRaw();
        var reusedLength = reused.Length;
        var isGarbage = false;
        foreach (var b in reused)
            isGarbage |= b == (byte)'Z';
        reader.Dispose();

        await Assert.That(reusedLength).IsZero();
        await Assert.That(isGarbage).IsFalse();
    }
}
