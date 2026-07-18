// Regression tests for inner helper naming (root cause of the a9d3e19 build break):
// InnerClassName must return a namespace-qualified name so call sites emitted into
// OTHER namespaces (e.g. top-level List<T> serializers, which live in the global
// namespace due to generic hint names) resolve the helper correctly.
//
// - Without AssemblyPrefix (non-JSON generators): legacy '<ns>.<SafeName><suffix>'
//   form, matching helper files that declare the type's own namespace.
// - With AssemblyPrefix (JSON generator): '<prefix>.<SafeName><suffix>' form,
//   matching helper files that declare the prefix namespace.

namespace PicoJetson.Tests;

/// <summary>
/// Model with a <c>Dictionary&lt;string, object?&gt;</c> property. Its mere presence
/// forces the SG to emit __PicoAnyDictHelper and call sites referencing it — if the
/// call sites are not namespace-qualified to match the helper's namespace, this
/// project fails to compile (CS0103).
/// </summary>
public class AnyDictHelperModel
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, object?> Extra { get; set; } = [];
}

[NotInParallel("GenInfrastructure.AssemblyPrefix")]
public sealed class InnerHelperNameTests
{
    /// <summary>Runs an action with AssemblyPrefix temporarily set, then restores it.</summary>
    private static string WithPrefix(string? prefix, Func<string> action)
    {
        var saved = PicoSerDe.Gen.GenInfrastructure.AssemblyPrefix;
        try
        {
            PicoSerDe.Gen.GenInfrastructure.AssemblyPrefix = prefix;
            return action();
        }
        finally
        {
            PicoSerDe.Gen.GenInfrastructure.AssemblyPrefix = saved;
        }
    }

    // ── The a9d3e19 regression: no-prefix callers need the namespace-qualified form ──

    [Test]
    public async Task InnerClassName_NoPrefix_IsNamespaceQualified()
    {
        var name = WithPrefix(
            null,
            () =>
                PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "MsgPackInner",
                    "PicoMsgPack.Tests.Address"
                )
        );
        // Helper files of the non-JSON generators declare 'namespace PicoMsgPack.Tests;'
        // so the reference must carry that namespace — a bare short name only resolves
        // from files that happen to live in the same namespace.
        await Assert
            .That(name)
            .IsEqualTo("PicoMsgPack.Tests.PicoMsgPack_Tests_AddressMsgPackInner");
    }

    [Test]
    public async Task InnerClassName_WithPrefix_IsPrefixQualified()
    {
        var name = WithPrefix(
            "__PicoSerDe_MyAsm",
            () =>
                PicoSerDe.Gen.GenInfrastructure.InnerClassName(
                    "JsonInner",
                    "PicoMsgPack.Tests.Address"
                )
        );
        await Assert.That(name).IsEqualTo("__PicoSerDe_MyAsm.PicoMsgPack_Tests_AddressJsonInner");
    }

    [Test]
    public async Task InnerClassName_NoPrefix_GlobalNamespaceType_BareName()
    {
        var name = WithPrefix(
            null,
            () => PicoSerDe.Gen.GenInfrastructure.InnerClassName("JsonInner", "SoloType")
        );
        await Assert.That(name).IsEqualTo("SoloTypeJsonInner");
    }

    [Test]
    public async Task InnerClassName_GenericTypeName_EmitsGlobalScope_RegardlessOfPrefix()
    {
        const string generic = "System.Collections.Generic.List<Foo.Bar>";
        var without = WithPrefix(
            null,
            () => PicoSerDe.Gen.GenInfrastructure.InnerClassName("JsonInner", generic)
        );
        var with = WithPrefix(
            "__PicoSerDe_MyAsm",
            () => PicoSerDe.Gen.GenInfrastructure.InnerClassName("JsonInner", generic)
        );
        await Assert.That(without).IsEqualTo(with);
        await Assert.That(without).StartsWith("global::");
        await Assert.That(without).EndsWith("JsonInner");
        await Assert.That(without).DoesNotContain("<");
    }

    // ── __PicoAnyDictHelper: compiling this project already proves the call sites
    //    resolve; this test additionally locks runtime round-trip behavior ──

    [Test]
    public async Task AnyDictModel_RoundTrips()
    {
        var model = new AnyDictHelperModel
        {
            Name = "probe",
            Extra = new Dictionary<string, object?>
            {
                ["s"] = "v",
                ["n"] = 42L,
                ["b"] = true,
                ["nil"] = null,
            },
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model);
        var json = Encoding.UTF8.GetString(bytes);
        await Assert.That(json).Contains("\"probe\"");

        var back = JsonSerializer.Deserialize<AnyDictHelperModel>(bytes);
        await Assert.That(back).IsNotNull();
        await Assert.That(back!.Extra.Count).IsEqualTo(4);
    }
}
