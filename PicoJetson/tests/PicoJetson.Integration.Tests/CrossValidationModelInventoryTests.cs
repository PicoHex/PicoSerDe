using PicoSerDe.Core;

namespace PicoJetson.Tests;

/// <summary>
/// The inventory of <see cref="ComplexModel"/> — the model the JSON cross-validation suite compares
/// against <c>System.Text.Json</c> — and the executable answer to audit C8, which read it and
/// <c>MultiFormatRoundTripTests.AllFormatsModel</c> (tests/PicoSerDe.Integration.Tests) as one thing
/// written twice. They are 21 members vs 18, and the four that exist only here are the JSON-only
/// collection surface; a merge in either direction would delete members from a regression surface
/// with no test going red. Its mirror is <c>AllFormatsModelInventoryTests</c>.
/// <para>
/// Every member is named with <c>nameof</c>, so deleting or renaming one stops the build (stronger
/// than reflection, and it survives <c>PublishAot</c>, where <c>GetProperties</c> would not); the
/// count is pinned, so an addition is a deliberate edit. This file pins shape only — the behaviour
/// assertions live in <c>JsonCrossValidationTests</c>.
/// </para>
/// </summary>
public class CrossValidationModelInventoryTests
{
    private static readonly string[] ModelMembers =
    [
        nameof(ComplexModel.Bool),
        nameof(ComplexModel.Int),
        nameof(ComplexModel.Long),
        nameof(ComplexModel.Float),
        nameof(ComplexModel.Double),
        nameof(ComplexModel.Decimal),
        nameof(ComplexModel.String),
        nameof(ComplexModel.NullableString),
        nameof(ComplexModel.DateTime),
        nameof(ComplexModel.TimeSpan),
        nameof(ComplexModel.DateOnly),
        nameof(ComplexModel.TimeOnly),
        nameof(ComplexModel.Guid),
        nameof(ComplexModel.Enum),
        nameof(ComplexModel.NullableInt),
        nameof(ComplexModel.IntList),
        nameof(ComplexModel.StringList),
        nameof(ComplexModel.IntArray),
        nameof(ComplexModel.StringDict),
        nameof(ComplexModel.Nested),
        nameof(ComplexModel.NestedList),
    ];

    [Test]
    public async Task ComplexModel_HasThePinned21Members()
    {
        await Assert.That(ModelMembers.Length).IsEqualTo(21);
        await Assert.That(ModelMembers.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(21);
        await Assert.That(nameof(SubModel.Name)).IsEqualTo("Name");
        await Assert.That(nameof(SubModel.Value)).IsEqualTo("Value");
    }

    /// <summary>
    /// The four members that must never be lost in a "these two models look alike, merge them"
    /// refactor: they are the collection shapes INI/TOML/YAML cannot express, i.e. the reason the
    /// two models are two.
    /// </summary>
    [Test]
    public async Task TheJsonOnlyCollectionMembersAreStillHere()
    {
        string[] jsonOnly =
        [
            nameof(ComplexModel.IntList),
            nameof(ComplexModel.IntArray),
            nameof(ComplexModel.StringDict),
            nameof(ComplexModel.NestedList),
        ];

        await Assert.That(jsonOnly.All(member => ModelMembers.Contains(member))).IsTrue();
    }

    /// <summary>
    /// The factory populates 19 of 21 members; the two nulls are the null-path coverage, so a
    /// factory that quietly stopped setting a member would turn the round-trip assertions into
    /// "default against default" comparisons that still pass.
    /// </summary>
    [Test]
    public async Task TheFactoryPopulates19Of21Members()
    {
        var model = ComplexModelFactory.Create();

        object?[] values =
        [
            model.Bool,
            model.Int,
            model.Long,
            model.Float,
            model.Double,
            model.Decimal,
            model.String,
            model.DateTime,
            model.TimeSpan,
            model.DateOnly,
            model.TimeOnly,
            model.Guid,
            model.Enum,
            model.IntList,
            model.StringList,
            model.IntArray,
            model.StringDict,
            model.Nested,
            model.NestedList,
        ];

        await Assert
            .That(values.Count(value => !Equals(value, default(object?))))
            .IsEqualTo(19)
            .Because("NullableString and NullableInt are null on purpose — that is the null path");
        await Assert.That(model.NullableString).IsNull();
        await Assert.That(model.NullableInt).IsNull();
    }
}
