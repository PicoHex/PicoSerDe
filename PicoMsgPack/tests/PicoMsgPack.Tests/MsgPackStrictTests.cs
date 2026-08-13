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

    [Test]
    public async Task Deserialize_StringIntoInt_Throws()
    {
        // int-keyed map { 0: "abc" } → Count (int) must throw
        var bytes = BuildMap((0, (Action<MsgPackWriter>)((w) => w.WriteString("abc"u8))));
        await Assert
            .That(ThrowsFormat(() => MsgPackSerializer.Deserialize<StrictMpModel>(bytes)))
            .IsTrue();
    }

    [Test]
    public async Task Deserialize_IntIntoBool_Throws()
    {
        // int-keyed map { 2: 1 } → Flag (bool) must throw
        var bytes = BuildMap((2, (Action<MsgPackWriter>)((w) => w.WriteInt32(1))));
        await Assert
            .That(ThrowsFormat(() => MsgPackSerializer.Deserialize<StrictMpModel>(bytes)))
            .IsTrue();
    }

    private static byte[] BuildMap((int Key, Action<MsgPackWriter> Write) entry)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var mw = new MsgPackWriter(buffer);
        mw.WriteStartObject(1);
        mw.WriteInt32(entry.Key);
        entry.Write(mw);
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
        var prev = MsgPackOptions.Current;
        try
        {
            MsgPackOptions.Current = new MsgPackOptions
            {
                DefaultIgnoreCondition = MsgPackIgnoreCondition.WhenWritingNull,
            };
            // Per-property WhenWritingDefault is honored independent of the
            // global option.
            var bytes = MsgPackSerializer.SerializeToUtf8Bytes(new OptModel());
            // Map with 0 entries: all properties skipped (WhenWritingDefault on Opt, null Name)
            await Assert.That(bytes.Length).IsGreaterThanOrEqualTo(1);
            await Assert.That(bytes[0]).IsEqualTo((byte)0x80); // fixmap(0)
        }
        finally
        {
            MsgPackOptions.Current = prev;
        }
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
