namespace PicoJetson.Tests;

public class NumberBufferDto
{
    public double Value { get; set; }
}

/// <summary>
/// BUG-11 regression: sequence-mode number tokens longer than the internal
/// buffer must not overflow it (IndexOutOfRangeException).
/// </summary>
public class NumberBufferTests
{
    [Test]
    public async Task LongNumber_SplitAcrossChunks_ParsesWithoutIndexOutOfRange()
    {
        JsonSerializer.Serialize(new NumberBufferDto()); // trigger generation
        var payload = "{\"Value\":" + new string('9', 60) + "}";
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        var dto = await JsonSerializer.DeserializeFromStreamAsync<NumberBufferDto>(ms);
        await Assert.That(dto!.Value).IsGreaterThan(1e59);
    }
}
