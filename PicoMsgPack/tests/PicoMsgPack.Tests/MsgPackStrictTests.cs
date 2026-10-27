namespace PicoMsgPack.Tests;

[PicoSerializable]
public class StrictMpModel
{
    public int Count { get; set; }
    public string? Name { get; set; }
    public bool Flag { get; set; }
    public int? Opt { get; set; }
}

/// <summary>
/// C2/H4 regression tests for the MsgPack generated deserializer: wrong-typed
/// values must throw, and WhenWritingDefault must skip default-valued members
/// (including Nullable&lt;T&gt; holding default(T)).
/// </summary>
public class MsgPackStrictTests
{
    private static bool ThrowsFormat(Action a)
    {
        try
        {
            a();
            return false;
        }
        catch (FormatException)
        {
            return true;
        }
    }

    // MsgPackWriter is a ref struct whose span cursor (_spanPos) is a *value* field:
    // handing it to a delegate by value writes the token into the copy's cursor, and those
    // bytes are never committed -- BuildMap then returns a truncated `81 00` instead of
    // `81 00 A3 61 62 63`. Hence the by-ref delegate below.
    private delegate void WriterAction(ref MsgPackWriter w);

    [Test]
    public async Task Deserialize_StringIntoInt_Throws()
    {
        // int-keyed map { 0: "abc" } → Count (int) must throw
        var bytes = BuildMap(0, (ref MsgPackWriter w) => w.WriteString("abc"u8));
        // Payload first: fixmap(1) { 0: fixstr(3) "abc" }, byte for byte. Without this
        // layer the test could go green for the wrong reason -- a truncated payload also
        // throws FormatException ("ran out of input"), not "wrong type".
        await Assert.That(bytes).IsEquivalentTo(new byte[] { 0x81, 0x00, 0xA3, 0x61, 0x62, 0x63 });
        await Assert
            .That(ThrowsFormat(() => MsgPackSerializer.Deserialize<StrictMpModel>(bytes)))
            .IsTrue();
    }

    [Test]
    public async Task Deserialize_IntIntoBool_Throws()
    {
        // int-keyed map { 2: 1 } → Flag (bool) must throw
        var bytes = BuildMap(2, (ref MsgPackWriter w) => w.WriteInt32(1));
        // Payload first: fixmap(1) { 2: 1 }; see the note above for why.
        await Assert.That(bytes).IsEquivalentTo(new byte[] { 0x81, 0x02, 0x01 });
        await Assert
            .That(ThrowsFormat(() => MsgPackSerializer.Deserialize<StrictMpModel>(bytes)))
            .IsTrue();
    }

    private static byte[] BuildMap(int key, WriterAction write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var mw = new MsgPackWriter(buffer);
        mw.WriteStartObject(1);
        mw.WriteInt32(key);
        write(ref mw); // by ref: the caller's writer keeps the span cursor
        mw.WriteEndObject();
        return buffer.WrittenSpan.ToArray();
    }

    [Test]
    public async Task Deserialize_ValidInput_StillWorks()
    {
        var src = new StrictMpModel
        {
            Count = 3,
            Name = "n",
            Flag = true,
        };
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(src);
        var back = MsgPackSerializer.Deserialize<StrictMpModel>(bytes);
        await Assert.That(back!.Count).IsEqualTo(3);
        await Assert.That(back.Name).IsEqualTo("n");
        await Assert.That(back.Flag).IsTrue();
    }

    [Test]
    public async Task WhenWritingDefault_NullableHoldingDefault_IsOmitted()
    {
        var opts = new MsgPackOptions
        {
            DefaultIgnoreCondition = MsgPackIgnoreCondition.WhenWritingNull,
        };
        // Per-property WhenWritingDefault is honored independent of the
        // global option.
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(new OptModel(), opts);
        // Map with 0 entries: all properties skipped (WhenWritingDefault on Opt, null Name)
        await Assert.That(bytes.Length).IsGreaterThanOrEqualTo(1);
        await Assert.That(bytes[0]).IsEqualTo((byte)0x80); // fixmap(0)
    }
}

[PicoSerializable]
public class OptModel
{
    [PicoIgnore(Condition = PicoIgnoreCondition.WhenWritingDefault)]
    public int? Opt { get; set; }

    [PicoIgnore(Condition = PicoIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }
}
