// The Tomlyn side of this benchmark: a typed, source-generated serialization context.
//
// Tomlyn 2.10's reflection overloads (TomlSerializer.Serialize<T>(T) / Deserialize<T>(string))
// are marked [RequiresDynamicCode]/[RequiresUnreferencedCode] and throw outright under
// NativeAOT ("Reflection serialization is disabled and no TOML metadata was found for type
// ..."). The source-generated path below is the one that works in both execution modes, and
// it is what makes the Tomlyn column an equivalent arm: one library call -> the same POCO.
//
// The 10 lines of context declaration are a *configuration* cost (paid once, at compile
// time), not a per-operation cost, so they do not enter the measurement. PicoToml pays the
// same kind of cost, except its analyzer emits the mapping without a hand-written class.

using Tomlyn;
using Tomlyn.Serialization;

/// <summary>
/// Tomlyn serialization context for the four benchmark models — the equivalent counterpart of
/// PicoToml's source generator, i.e. "both sides declare their types, both sides emit code".
/// </summary>
[TomlSerializable(typeof(SimplePoco))]
[TomlSerializable(typeof(MediumScalarPoco))]
[TomlSerializable(typeof(NestedPoco))]
[TomlSerializable(typeof(LargeFlatPoco))]
[TomlSourceGenerationOptions]
internal sealed partial class TomlynBenchContext : TomlSerializerContext
{
    public TomlynBenchContext()
        : base(TomlSerializerOptions.Default) { }
}
