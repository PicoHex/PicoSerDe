using System.Text;

namespace PicoJetson.Tests;

// These types are used ONLY through the discovery entry points under test —
// if the generator's candidate list misses an entry point, no serializer is
// generated and the call fails at runtime.
public class DiscoveryLineOnly
{
    public int V { get; set; }
}

public class DiscoveryEnumerationOnly
{
    public int N { get; set; }
}

public class DiscoveryStreamOnly
{
    public string S { get; set; } = "";
}

public class DiscoveryTests
{
    [Test]
    public async Task SerializeLines_OnlyUsage_Generates()
    {
        var bytes = JsonSerializer.SerializeLines(new[] { new DiscoveryLineOnly { V = 7 } });
        await Assert.That(Encoding.UTF8.GetString(bytes)).Contains("\"V\":7");
    }

    [Test]
    public async Task DeserializeLines_OnlyUsage_Generates()
    {
        var items = JsonSerializer.DeserializeLines<DiscoveryEnumerationOnly>(
            "{\"N\":1}\n{\"N\":2}\n"u8
        );
        await Assert.That(items.Length).IsEqualTo(2);
        await Assert.That(items[1]!.N).IsEqualTo(2);
    }

    [Test]
    public async Task DeserializeFromStreamAsync_OnlyUsage_Generates()
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes("""{"S":"x"}"""));
        var dto = await JsonSerializer.DeserializeFromStreamAsync<DiscoveryStreamOnly>(ms);
        await Assert.That(dto!.S).IsEqualTo("x");
    }
}
