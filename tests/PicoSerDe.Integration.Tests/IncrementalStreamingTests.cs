using PicoIni;
using PicoToml;
using PicoYaml;

namespace PicoSerDe.Integration.Tests;

/// <summary>
/// Streaming registration contract per format:
/// - TOML parses incrementally (explicit incomplete signal + member-level
///   resume); the chunk matrix runs on the incremental path.
/// - YAML/INI currently stream document-oriented: the payload is buffered and
///   the synchronous parser runs (results are identical to the sync path).
/// Incremental resume for YAML/INI needs the block/section state carried
/// through the generated delegate (follow-up); these assertions lock the
/// current contract so the switch to incremental is explicit.
/// </summary>
public class IncrementalStreamingTests
{
    [Test]
    public async Task Toml_HasStreamingDelegate_IsTrue()
    {
        await Assert.That(TomlSerializer.HasStreamingDelegate<SkDoc>()).IsTrue();
    }

    [Test]
    public async Task Yaml_StreamsDocumentOriented()
    {
        await Assert.That(YamlSerializer.HasStreamingDelegate<SkDoc>()).IsFalse();
    }

    [Test]
    public async Task Ini_StreamsDocumentOriented()
    {
        await Assert.That(IniSerializer.HasStreamingDelegate<SkDoc>()).IsFalse();
    }
}
