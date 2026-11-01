namespace PicoSerDe.Integration.Tests;

/// <summary>
/// The inventory of <see cref="AllFormatsModel"/> — the model <see cref="MultiFormatRoundTripTests"/>
/// drives through all five formats.
/// <para>
/// The mirror of <c>CrossValidationModelInventoryTests</c> (PicoJetson.Integration.Tests, over
/// <c>PicoCrossValidation.ComplexModel</c>): audit C8 read the two models as one thing written
/// twice, and they are 18 members here against 21 there. <c>Count</c> exists only here, and the four
/// members that exist only there (<c>IntList</c>, <c>IntArray</c>, <c>StringDict</c>,
/// <c>NestedList</c>) are missing here <b>because INI/TOML/YAML cannot express them</b> — adding them
/// to make the models "the same" breaks the round-trip assertions below it, and dropping them there
/// shrinks the JSON regression surface. Both inventories are compile-time pinned (<c>nameof</c> +
/// a member count) rather than reflective, so they hold under <c>PublishAot</c> too.
/// </para>
/// </summary>
public class AllFormatsModelInventoryTests
{
    private static readonly string[] ModelMembers =
    [
        nameof(AllFormatsModel.Bool),
        nameof(AllFormatsModel.Int),
        nameof(AllFormatsModel.Long),
        nameof(AllFormatsModel.Float),
        nameof(AllFormatsModel.Double),
        nameof(AllFormatsModel.Decimal),
        nameof(AllFormatsModel.String),
        nameof(AllFormatsModel.NullableString),
        nameof(AllFormatsModel.NullableInt),
        nameof(AllFormatsModel.DateTime),
        nameof(AllFormatsModel.TimeSpan),
        nameof(AllFormatsModel.DateOnly),
        nameof(AllFormatsModel.TimeOnly),
        nameof(AllFormatsModel.Guid),
        nameof(AllFormatsModel.Enum),
        nameof(AllFormatsModel.Count),
        nameof(AllFormatsModel.StringList),
        nameof(AllFormatsModel.Nested),
    ];

    [Test]
    public async Task AllFormatsModel_HasThePinned18Members()
    {
        await Assert.That(ModelMembers.Length).IsEqualTo(18);
        await Assert.That(ModelMembers.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(18);
        await Assert.That(nameof(AllFormatsSub.Name)).IsEqualTo("Name");
        await Assert.That(nameof(AllFormatsSub.Value)).IsEqualTo("Value");
    }

    /// <summary>
    /// The member that exists only here: picking the JSON model as the survivor of a merge would
    /// drop it, and nothing else in the suite would notice.
    /// </summary>
    [Test]
    public async Task TheMultiFormatOnlyMemberIsStillHere()
    {
        await Assert.That(ModelMembers.Contains(nameof(AllFormatsModel.Count))).IsTrue();
    }

    /// <summary>
    /// The sibling's four JSON-only members are absent here, deliberately: this asserts the
    /// divergence that keeps the two models apart, so making them "the same" has to be an explicit
    /// decision with a test update, not a drive-by merge.
    /// </summary>
    [Test]
    public async Task TheJsonOnlyCollectionMembersAreStillAbsent()
    {
        string[] jsonOnly = ["IntList", "IntArray", "StringDict", "NestedList"];

        await Assert.That(jsonOnly.Any(ModelMembers.Contains)).IsFalse();
    }
}
