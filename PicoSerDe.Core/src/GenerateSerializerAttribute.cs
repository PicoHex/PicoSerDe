namespace PicoSerDe.Core;

/// <summary>
/// Short-hand alias for <c>[PicoSerializable(typeof(T))]</c>.
/// Marks a type for serialization code generation across all referenced PicoSerDe format modules.
///
/// Usage:
/// <code>
/// [GenerateSerializer(typeof(UserDto))]
/// [GenerateSerializer(typeof(ProductDto))]
/// class PicoSerDeConfig { }
/// </code>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class GenerateSerializerAttribute : PicoSerializableAttribute
{
    public GenerateSerializerAttribute(Type type) : base(type) { }
}
