using PicoSerDe.Core;

namespace PicoSerDe.Core.Tests;

public ref struct SmokeRef
{
    public int X;
}

public class SerDelegateTests
{
    [Test]
    public async Task SerDelegate_Accepts_RefStruct_AtCompileTime()
    {
        // Compile-time verification: SerDelegate<SmokeRef> is a valid type.
        // We can declare a variable of this delegate type pointing to a ref struct.
        SerDelegate<SmokeRef> handler = static (_, _, _) => { };
        await Assert.That(handler).IsNotNull();
    }

    [Test]
    public async Task SerDelegate_CarriesOptionsSlot()
    {
        SerOptions? captured = null;
        SerDelegate<int> d = (writer, value, options) => captured = options;
        var opts = new SerOptions();
        d(new ArrayBufferWriter<byte>(), 42, opts);
        await Assert.That(captured).IsEqualTo(opts);
    }

    [Test]
    public async Task SerDelegate_OptionsSlotIsNull_WhenNotProvided()
    {
        SerOptions? captured = new SerOptions();
        SerDelegate<int> d = (writer, value, options) => captured = options;
        d(new ArrayBufferWriter<byte>(), 1, null);
        await Assert.That(captured).IsNull();
    }
}
