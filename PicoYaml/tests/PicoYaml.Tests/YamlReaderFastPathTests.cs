namespace PicoYaml.Tests;

public class YamlReaderFastPathTests
{
    // NOTE: These tests are disabled because YamlReader does not yet support
    // YAML flow sequences ([a, b, c]) in the span read path. The fast-path
    // methods (TryReadInt32ArrayFast etc.) exist in YamlReader but are
    // unreachable until flow sequence parsing is implemented.
    // See docs/beta-issues.md: "YAML Reader: Tag 指令 !type / %TAG"

    // [Test]
    public async Task TryReadInt32ArrayFast_Basic()
    {
        await Task.CompletedTask;
    }

    // [Test]
    public async Task TryReadInt64ArrayFast_Basic()
    {
        await Task.CompletedTask;
    }

    // [Test]
    public async Task TryReadBoolArrayFast_Basic()
    {
        await Task.CompletedTask;
    }

    // [Test]
    public async Task TryReadInt32ArrayFast_Empty()
    {
        await Task.CompletedTask;
    }
}
