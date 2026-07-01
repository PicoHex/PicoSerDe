// PicoSerDe.Core/src/PicoDerivedTypeAttribute.cs
namespace PicoSerDe.Core;

/// <summary>
/// Declares a derived type and its discriminator value for a base type.
/// Must be placed on the base type alongside <see cref="PicoSerializableAttribute"/>.
/// Multiple allowed — one per derived type.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true)]
public sealed class PicoDerivedTypeAttribute : Attribute
{
    public Type DerivedType { get; }
    public string TypeDiscriminator { get; }

    public PicoDerivedTypeAttribute(Type derivedType, string typeDiscriminator)
    {
        DerivedType = derivedType;
        TypeDiscriminator = typeDiscriminator;
    }
}
