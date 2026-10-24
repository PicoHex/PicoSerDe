namespace PicoJetson.Tests;

[PicoDerivedType(typeof(DeclaredLeaf), "leaf")]
public abstract class PolyBase
{
    public int Id { get; set; }
}

public class DeclaredLeaf : PolyBase
{
    public string Name { get; set; } = "";
}

public class UndeclaredLeaf : PolyBase
{
    public string Other { get; set; } = "";
}

[PicoDerivedType(typeof(PolyCtorLeaf), "leaf")]
public abstract class PolyNode { }

public class PolyCtorLeaf : PolyNode
{
    [JsonConstructor]
    public PolyCtorLeaf(string label, int count)
    {
        Label = label;
        Count = count;
    }

    public string Label { get; }
    public int Count { get; }
}

/// <summary>
/// Object that carries a polymorphic property. The poly inner helper writes the
/// derived value straight into <c>jw.Buffer</c>, so the pending-property-name flag
/// has to be cleared explicitly (the value never reaches BeforeWriteValue).
/// </summary>
public class PolyHolder
{
    public int Head { get; set; }
    public PolyBase Item { get; set; } = new DeclaredLeaf();
    public int Tail { get; set; }
}

/// <summary>
/// Poly property written LAST: nothing after it consumes the pending flag, so
/// closing the object is the failure site (the value never reaches
/// BeforeWriteValue because it goes through jw.Buffer).
/// </summary>
public class PolyTailHolder
{
    public int Head { get; set; }
    public PolyBase Item { get; set; } = new DeclaredLeaf();
}

public class PolyContractTests
{
    [Test]
    public async Task UndeclaredDerivedType_Throws()
    {
        await Assert.ThrowsAsync<FormatException>(async () =>
            await Task.Run(() => JsonSerializer.Serialize<PolyBase>(new UndeclaredLeaf()))
        );
    }

    [Test]
    public async Task DeclaredDerivedType_StillWorks() =>
        await Assert
            .That(JsonSerializer.Serialize<PolyBase>(new DeclaredLeaf { Id = 1, Name = "x" }))
            .Contains("\"Name\":\"x\"");

    [Test]
    public async Task PolyStreaming_CtorParamsSurviveChunkBoundary()
    {
        var json = JsonSerializer.Serialize<PolyNode>(new PolyCtorLeaf("label", 7));
        using var stream = new OneByteChunkStream(Encoding.UTF8.GetBytes(json));
        var back = await JsonSerializer.DeserializeFromStreamAsync<PolyNode>(stream);
        await Assert.That(back).IsTypeOf<PolyCtorLeaf>();
        await Assert.That(((PolyCtorLeaf)back).Label).IsEqualTo("label");
        await Assert.That(((PolyCtorLeaf)back).Count).IsEqualTo(7);
    }

    [Test]
    public async Task PolyProperty_InsideObject_SerializesAndRoundTrips()
    {
        // "Item" is a poly property: the generated code delegates to
        // PolyBaseJsonInner.Serialize, which writes into jw.Buffer. Closing the
        // enclosing object used to throw (pending property name) and, when it did
        // not, the following "Tail" property silently lost its separator.
        var json = JsonSerializer.Serialize(
            new PolyHolder
            {
                Head = 1,
                Item = new DeclaredLeaf { Id = 2, Name = "x" },
                Tail = 3,
            }
        );

        await Assert.That(json).Contains("\"Item\":{");
        await Assert.That(json).Contains("\"Tail\":3");

        var back = JsonSerializer.Deserialize<PolyHolder>(Encoding.UTF8.GetBytes(json));
        await Assert.That(back).IsNotNull();
        await Assert.That(back!.Head).IsEqualTo(1);
        await Assert.That(back!.Item).IsTypeOf<DeclaredLeaf>();
        await Assert.That(back!.Tail).IsEqualTo(3);
    }

    [Test]
    public async Task PolyProperty_LastInObject_ClosesObject()
    {
        var json = JsonSerializer.Serialize(
            new PolyTailHolder
            {
                Head = 1,
                Item = new DeclaredLeaf { Id = 2, Name = "x" },
            }
        );

        await Assert.That(json).Contains("\"Item\":{");
        await Assert.That(json).EndsWith("}");

        var back = JsonSerializer.Deserialize<PolyTailHolder>(Encoding.UTF8.GetBytes(json));
        await Assert.That(back).IsNotNull();
        await Assert.That(back!.Head).IsEqualTo(1);
        await Assert.That(back!.Item).IsTypeOf<DeclaredLeaf>();
    }
}

/// <summary>Reads one byte per <see cref="ReadAsync"/> call to force chunk-boundary resumes.</summary>
internal sealed class OneByteChunkStream : Stream
{
    private readonly byte[] _data;
    private int _pos;

    internal OneByteChunkStream(byte[] data) => _data = data;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _data.Length;

    public override long Position
    {
        get => _pos;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_pos >= _data.Length || buffer.Length == 0)
            return 0;
        buffer[0] = _data[_pos++];
        return 1;
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    ) => ValueTask.FromResult(Read(buffer.Span));

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    ) => Task.FromResult(Read(buffer.AsSpan(offset, count)));

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();
}
