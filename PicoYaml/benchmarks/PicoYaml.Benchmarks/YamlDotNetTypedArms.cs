// The YamlDotNet typed arm — CoreCLR (JIT) only.
//
// Under CoreCLR, YamlDotNet 18.1.0's typed API is exactly one library call in and one out, so
// it is a fair counterpart to YamlSerializer.Serialize / Deserialize<T>. Under NativeAOT it is
// not: the same two calls do not produce an answer at all. Measured on this repository's
// benchmark models (win-x64, NativeAOT, net10.0):
//
//   Deserialize<T>(text) -> YamlDotNet.Core.YamlException: Exception during deserialization
//        <- System.InvalidOperationException: Failed to create an instance of type 'SimplePoco'.
//        <- System.MissingMethodException: No parameterless constructor defined for type 'SimplePoco'.
//   Serialize(poco)      -> no exception, and the document is `{}` — a silent empty result,
//                           which is worse than a throw because the timing looks fine.
//
// So the benchmark only builds this arm when BenchProtocol.Mode is Jit (the AOT table prints
// those two outcomes instead of a number). The two builders are built ONCE, here, at type
// initialisation: constructing SerializerBuilder/DeserializerBuilder inside the measured
// delegate would time the builder (measured ~80-120 µs/op with it inside, ~4-7 µs/op of actual
// node work) and the row would not be measuring the serializer at all.
//
// IL3050 is suppressed on these two lines and nowhere else: the warning is true (both builders
// are [RequiresDynamicCode]) and it is precisely why this arm is JIT-only. Suppressing it keeps
// the build honest at zero warnings while the reason lives in the code that causes it — the
// runtime evidence above is in the AOT branch of Program.cs, printed on every AOT run.
#pragma warning disable IL3050 // YamlDotNet's reflection builders: JIT-only arm, see the header.

using YamlDotNet.Serialization;

internal static class YamlDotNetTypedArms
{
    private static readonly ISerializer DotNetSerializer = new SerializerBuilder().Build();
    private static readonly IDeserializer DotNetDeserializer = new DeserializerBuilder().Build();

    public static string Serialize<T>(T value) => DotNetSerializer.Serialize(value);

    public static T Deserialize<T>(string text) => DotNetDeserializer.Deserialize<T>(text);
}

#pragma warning restore IL3050
