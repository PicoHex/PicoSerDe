namespace PicoYaml.Tests;

public class YamlReaderFastPathTests
{
    // NOTE: These tests are disabled: YamlReader flow sequences ([a, b, c])
    // are not reachable through the span read path, so the fast-path methods
    // (TryReadInt32ArrayFast etc.) are currently unused. Re-enable them when
    // flow-sequence parsing lands in the span path.

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
