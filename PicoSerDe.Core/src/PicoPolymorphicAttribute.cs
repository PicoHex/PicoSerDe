// PicoSerDe.Core/src/PicoPolymorphicAttribute.cs
namespace PicoSerDe.Core;

/// <summary>
/// Configures polymorphic type discrimination for a base type.
/// When present on a type that also has <see cref="PicoDerivedTypeAttribute"/>,
/// the SG generates discriminator write/read logic for all derived types.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class PicoPolymorphicAttribute : Attribute
{
    /// <summary>Discriminator property name. Default is <c>"$type"</c>.</summary>
    public string TypeDiscriminatorPropertyName { get; set; } = "$type";
}
