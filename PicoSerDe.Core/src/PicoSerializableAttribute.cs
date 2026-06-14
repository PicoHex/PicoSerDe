namespace PicoSerDe.Core;

/// <summary>
/// Mark a type as needing serialization support from all referenced PicoSerDe format modules.
///
/// Two usage modes:
/// <code>
/// // Mode 1: direct — generate serializers for the marked type itself
/// [PicoSerializable]
/// public class UserDto { ... }
///
/// // Mode 2: indirect — generate serializers for a type from any assembly
/// [PicoSerializable(typeof(ExternalLibrary.SharedDto))]
/// class Config { }
/// </code>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true)]
public class PicoSerializableAttribute : Attribute
{
    /// <summary>The type to generate serializers for, or <c>null</c> to use the target type itself.</summary>
    public Type? Type { get; }

    /// <summary>Mark the target type itself for serialization.</summary>
    public PicoSerializableAttribute() { }

    /// <summary>Mark <paramref name="type"/> for serialization (useful for types in referenced assemblies).</summary>
    public PicoSerializableAttribute(Type type) => Type = type;
}
