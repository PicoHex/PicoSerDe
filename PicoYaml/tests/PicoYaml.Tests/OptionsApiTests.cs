namespace PicoYaml.Tests;

/// <summary>
/// Review P2-2: the options classes must be reachable from the public API.
/// Passing options (defaults) must preserve the existing behavior.
/// </summary>
public class OptionsApiTests
{
    [Test]
    public async Task Serialize_AcceptsOptions()
    {
        var dto = new OptionsProbeDto { Name = "x", Title = null };
        var withOptions = YamlSerializer.Serialize(dto, new YamlOptions());
        var without = YamlSerializer.Serialize(dto);
        await Assert.That(withOptions).IsEqualTo(without);
    }

    [Test]
    public async Task Deserialize_AcceptsOptions()
    {
        var bytes = Encoding.UTF8.GetBytes(
            YamlSerializer.Serialize(new OptionsProbeDto { Name = "x" })
        );
        var dto = YamlSerializer.Deserialize<OptionsProbeDto>(bytes, new YamlOptions());
        await Assert.That(dto!.Name).IsEqualTo("x");
    }
}

public class OptionsProbeDto
{
    public string Name { get; set; } = "";
    public string? Title { get; set; }
}
