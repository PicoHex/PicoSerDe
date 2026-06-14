namespace PicoJetson;

/// <summary>
/// Mark a type as needing JSON serialization support from PicoJetson only.
///
/// Two usage modes:
/// <code>
/// // Direct: generate JSON serializer for the marked type
/// [PicoJsonSerializable]
/// public class JsonDto { ... }
///
/// // Indirect: generate JSON serializer for a type from any assembly
/// [PicoJsonSerializable(typeof(ExternalLibrary.SharedDto))]
/// class Config { }
/// </code>
/// </summary>
/// <remarks>
/// For multi-format serialization, use <see cref="PicoSerDe.Core.PicoSerializableAttribute"/>
/// which triggers all referenced format modules.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true)]
public sealed class PicoJsonSerializableAttribute : PicoSerDe.Core.PicoSerializableAttribute
{
    /// <summary>Mark the target type itself for JSON serialization.</summary>
    public PicoJsonSerializableAttribute() { }

    /// <summary>Mark <paramref name="type"/> for JSON serialization.</summary>
    public PicoJsonSerializableAttribute(Type type) : base(type) { }
}
