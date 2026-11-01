// The reference set for the compilations in THIS project that must see all five formats at once.
//
// It lives here, and not in shared/, because it names all five format assemblies: the other two
// test projects that compile generated code (PicoJetson.Integration.Tests, PicoToml.Tests) each
// reference a single format, so a shared file saying `typeof(PicoIni.IniSerializer)` would not even
// compile there. The platform half of the set — the part that was copied into fifteen files — is
// shared/RoslynProbe.cs; this file is only "which assemblies".
//
// Two users: SilentDropDiagnosticTests (which runs every format's generator) and
// ShapeCompilationTests, which deliberately compiles against the SAME set rather than growing its
// own — "a new runtime assembly cannot be missing here only".
using System.Reflection;
using Microsoft.CodeAnalysis;
using PicoSerDe.TestSupport;

namespace PicoSerDe.Integration.Tests;

internal static class AllFormatReferences
{
    private static readonly Assembly[] Assemblies =
    [
        typeof(PicoIni.IniSerializer).Assembly,
        typeof(PicoJetson.JsonSerializer).Assembly,
        typeof(PicoMsgPack.MsgPackSerializer).Assembly,
        typeof(PicoYaml.YamlSerializer).Assembly,
        typeof(PicoToml.TomlSerializer).Assembly,
        // [PicoSerializable(IncludeFields = true)] lives in PicoSerDe.Core: without this reference
        // the attribute binds to an error type, ForAttributeWithMetadataName finds nothing, and the
        // public-field extraction path is never reached.
        typeof(PicoSerDe.Core.PicoSerializableAttribute).Assembly,
    ];

    /// <summary>The five format assemblies plus <c>PicoSerDe.Core</c>, over Roslyn's filtered platform set.</summary>
    internal static IEnumerable<MetadataReference> References() =>
        RoslynProbe.PlatformReferences(Assemblies);
}
