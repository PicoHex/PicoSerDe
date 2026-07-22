// Contract tests for the cross-format serializer registry.
// Key invariant: registrations are isolated per format marker — the same T
// registered under one format must not leak into another.

namespace PicoSerDe.Core.Tests;

// Format markers and payloads dedicated to this file (static registry state
// is process-wide; type isolation keeps tests independent).
internal readonly struct RegFmtA { }

internal readonly struct RegFmtB { }

internal sealed class RegPayload
{
    public int V { get; set; }
}

internal sealed class RegPayloadDes : IDeserializer<RegPayload>
{
    public RegPayload Deserialize(ReadOnlySpan<byte> data) => new() { V = 1 };
}

internal ref struct RegRefPayload
{
    public int V;
}

public class SerRegistryTests
{
    [Test]
    public async Task Handler_IsolatedPerFormatMarker()
    {
        SerRegistry<RegFmtA, RegPayload>.Handler = null;
        SerRegistry<RegFmtB, RegPayload>.Handler = null;
        SerRegistry<RegFmtA, RegPayload>.Handler = static (w, v) => { };
        await Assert.That(SerRegistry<RegFmtA, RegPayload>.Handler).IsNotNull();
        await Assert.That(SerRegistry<RegFmtB, RegPayload>.Handler).IsNull();
    }

    [Test]
    public async Task CustomHandler_IndependentOfHandler()
    {
        SerRegistry<RegFmtA, RegPayload>.Handler = null;
        SerRegistry<RegFmtA, RegPayload>.CustomHandler = null;
        SerRegistry<RegFmtB, RegPayload>.Handler = null;
        SerRegistry<RegFmtB, RegPayload>.CustomHandler = null;
        SerRegistry<RegFmtB, RegPayload>.Handler = static (w, v) => { };
        await Assert.That(SerRegistry<RegFmtB, RegPayload>.CustomHandler).IsNull();

        SerRegistry<RegFmtB, RegPayload>.CustomHandler = static (w, v) => { };
        await Assert.That(SerRegistry<RegFmtB, RegPayload>.CustomHandler).IsNotNull();
        // Format isolation holds for the custom slot too
        await Assert.That(SerRegistry<RegFmtA, RegPayload>.CustomHandler).IsNull();
    }

    [Test]
    public async Task DesRegistry_IsolatedPerFormatMarker()
    {
        DesRegistry<RegFmtA, RegPayload>.Deserializer = null;
        DesRegistry<RegFmtB, RegPayload>.Deserializer = null;
        DesRegistry<RegFmtA, RegPayload>.Deserializer = new RegPayloadDes();
        await Assert.That(DesRegistry<RegFmtA, RegPayload>.Deserializer).IsNotNull();
        await Assert.That(DesRegistry<RegFmtB, RegPayload>.Deserializer).IsNull();
    }

    [Test]
    public async Task SerRegistry_SupportsRefStructPayloads()
    {
        // ref struct T must be usable (allows ref struct constraint)
        SerRegistry<RegFmtA, RegRefPayload>.Handler = null;
        SerRegistry<RegFmtA, RegRefPayload>.Handler = static (w, v) => { };
        await Assert.That(SerRegistry<RegFmtA, RegRefPayload>.Handler).IsNotNull();
    }
}
