namespace PicoJetson.Tests;

public class GenOptInner
{
    public string FullName { get; set; } = "";
}

public class GenOptOuter
{
    public GenOptInner Child { get; set; } = new();
}

public class GenOptSimpleDoc
{
    public string? Name { get; set; }
    public int Count { get; set; }
}

public class GeneratorOptionsTests
{

    [Test]
    public async Task Serialize_WithOptions_ThreadsToNestedTypes()
    {
        var opts = new JsonOptions
        {
            Indented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        };
        var result = JsonSerializer.Serialize(new GenOptOuter { Child = new GenOptInner { FullName = "x" } }, opts);
        // Naming policy applies to top-level properties; nested members keep their compiled names.
        await Assert.That(result).Contains("\"child\"");
        await Assert.That(result).Contains("\n");
    }

    [Test]
    public async Task Serialize_DefaultOptions_CompactAndPascalCase()
    {
        var result = JsonSerializer.Serialize(new GenOptOuter { Child = new GenOptInner { FullName = "x" } });
        await Assert.That(result).DoesNotContain("\"child\"");
        await Assert.That(result).DoesNotContain("\n");
    }

    [Test]
    public async Task Deserialize_WithUnmappedDisallow_Throws()
    {
        var opts = new JsonOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        var threw = false;
        try
        {
            JsonSerializer.Deserialize<GenOptSimpleDoc>("{ \"nope\": 1 }"u8, opts);
        }
        catch (FormatException)
        {
            threw = true;
        }
        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task Deserialize_Default_UnmappedSkipped()
    {
        var result = JsonSerializer.Deserialize<GenOptSimpleDoc>("{ \"nope\": 1, \"name\": \"a\" }"u8);
        await Assert.That(result!.Name).IsEqualTo("a");
    }

    [Test]
    public async Task Deserialize_WithCaseInsensitiveMatch()
    {
        var opts = new JsonOptions { PropertyNameCaseInsensitive = true };
        var result = JsonSerializer.Deserialize<GenOptSimpleDoc>("{ \"NAME\": \"a\" }"u8, opts);
        await Assert.That(result!.Name).IsEqualTo("a");
    }

    [Test]
    public async Task Deserialize_WithIgnoreCondition_WhenWritingNull()
    {
        var opts = new JsonOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        var json = JsonSerializer.Serialize(new GenOptSimpleDoc { Name = null!, Count = 3 }, opts);
        await Assert.That(json).DoesNotContain("Name");
        await Assert.That(json).Contains("Count");
    }
}
