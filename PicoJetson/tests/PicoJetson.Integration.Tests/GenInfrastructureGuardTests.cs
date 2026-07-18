// Contract tests for the shared null-guard emission helpers used by all
// five format generators (convergence of the DefaultIgnoreCondition work).

using System.Collections.Immutable;
using PicoSerDe.Gen;

namespace PicoJetson.Tests;

public sealed class GenInfrastructureGuardTests
{
    private static PropertyInfo Prop(
        string kind = "string",
        bool isNullable = false,
        bool isNrt = false
    ) =>
        new(
            "X",
            "X",
            kind,
            "string",
            isNullable,
            null,
            null,
            null,
            null,
            ImmutableArray<PropertyInfo>.Empty,
            null,
            IsNullableReference: isNrt
        );

    // ── IsConditionallyOmittable: single source of truth for guard eligibility ──

    [Test]
    public async Task Omittable_NullableReference_True()
    {
        await Assert
            .That(GenInfrastructure.IsConditionallyOmittable(Prop(isNullable: true, isNrt: true)))
            .IsTrue();
    }

    [Test]
    public async Task Omittable_NullableValueType_True()
    {
        await Assert
            .That(
                GenInfrastructure.IsConditionallyOmittable(
                    Prop(kind: "int32", isNullable: true, isNrt: false)
                )
            )
            .IsTrue();
    }

    [Test]
    public async Task Omittable_NullableCollection_True()
    {
        await Assert
            .That(
                GenInfrastructure.IsConditionallyOmittable(
                    Prop(kind: "array", isNullable: true, isNrt: true)
                )
            )
            .IsTrue();
    }

    [Test]
    public async Task Omittable_NonNullable_False()
    {
        await Assert.That(GenInfrastructure.IsConditionallyOmittable(Prop())).IsFalse();
    }

    // ── EmitNullGuardOpen: the always-omit guard (TOML/INI/YAML group) ──

    [Test]
    public async Task NullGuard_NullableProperty_EmitsGuardAndReturnsTrue()
    {
        var sb = new StringBuilder();
        var opened = GenInfrastructure.EmitNullGuardOpen(
            sb,
            Prop(isNullable: true, isNrt: true),
            "value.X",
            "    "
        );
        await Assert.That(opened).IsTrue();
        var nl = Environment.NewLine;
        await Assert.That(sb.ToString()).IsEqualTo($"    if (value.X != null){nl}    {{{nl}");
    }

    [Test]
    public async Task NullGuard_NullableValueType_UsesLiftedNullComparison()
    {
        var sb = new StringBuilder();
        var opened = GenInfrastructure.EmitNullGuardOpen(
            sb,
            Prop(kind: "int32", isNullable: true, isNrt: false),
            "v.Age",
            ""
        );
        await Assert.That(opened).IsTrue();
        await Assert.That(sb.ToString()).Contains("if (v.Age != null)");
    }

    [Test]
    public async Task NullGuard_NonNullable_EmitsNothingAndReturnsFalse()
    {
        var sb = new StringBuilder();
        var opened = GenInfrastructure.EmitNullGuardOpen(sb, Prop(), "value.X", "    ");
        await Assert.That(opened).IsFalse();
        await Assert.That(sb.Length).IsEqualTo(0);
    }

    // ── IsComplexMember: object/dict members handled by dedicated paths ──

    [Test]
    public async Task ComplexMember_ObjectAndDict_True()
    {
        await Assert.That(GenInfrastructure.IsComplexMember(Prop(kind: "object"))).IsTrue();
        await Assert.That(GenInfrastructure.IsComplexMember(Prop(kind: "dict"))).IsTrue();
    }

    [Test]
    public async Task ComplexMember_ScalarAndCollection_False()
    {
        await Assert.That(GenInfrastructure.IsComplexMember(Prop())).IsFalse();
        await Assert.That(GenInfrastructure.IsComplexMember(Prop(kind: "list"))).IsFalse();
    }
}
