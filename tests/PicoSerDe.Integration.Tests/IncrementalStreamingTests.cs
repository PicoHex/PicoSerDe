using PicoIni;
using PicoToml;
using PicoYaml;

namespace PicoSerDe.Integration.Tests;

/// <summary>
/// All text formats stream incrementally: the generated partial delegates are
/// registered, so the chunk matrix runs on the incremental path (member-level
/// Mark/RewindToMark + a transactional Read, both from
/// PicoSerDe.Core.ITransactionalTokenReader).
/// </summary>
public class IncrementalStreamingTests
{
    [Test]
    public async Task Toml_HasStreamingDelegate_IsTrue()
    {
        await Assert.That(TomlSerializer.HasStreamingDelegate<SkDoc>()).IsTrue();
    }

    [Test]
    public async Task Yaml_HasStreamingDelegate_IsTrue()
    {
        await Assert.That(YamlSerializer.HasStreamingDelegate<SkDoc>()).IsTrue();
    }

    [Test]
    public async Task Ini_HasStreamingDelegate_IsTrue()
    {
        await Assert.That(IniSerializer.HasStreamingDelegate<SkDoc>()).IsTrue();
    }
}
