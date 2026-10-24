namespace PicoIni.Tests;

/// <summary>
/// C-31 regression: a null dictionary member has no INI section to write, so
/// serialization must omit it instead of dereferencing null.
/// </summary>
public class IniNullableDictTests
{
    [Test]
    public async Task NullDictionary_IsOmitted() =>
        await Assert.That(IniSerializer.Serialize(new Holder()).Trim()).IsEmpty();

    [Test]
    public async Task NonNullDictionary_StillWrites() =>
        await Assert
            .That(IniSerializer.Serialize(new Holder { Map = new() { ["a"] = 1 } }))
            .Contains("[Map]");
}

public class Holder
{
    public Dictionary<string, int>? Map { get; set; }
}
