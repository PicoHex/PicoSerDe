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
        SerDelegate<SmokeRef> handler = static (_, _) => { };
        await Assert.That(handler).IsNotNull();
    }
}
