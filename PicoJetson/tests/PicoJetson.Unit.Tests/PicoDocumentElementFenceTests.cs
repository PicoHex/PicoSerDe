namespace PicoJetson.Tests;

/// <summary>
/// Fences for the DOM layer (<see cref="PicoElement"/>), the least-pinned region in the
/// repository: the third ablation wave broke 15 protections here and found 8 of them
/// unwatched — several of which hand back a <em>wrong value</em> instead of failing
/// (a past-the-end index reads as an empty element, a numeric string parses as a number,
/// a non-boolean reads as <c>false</c>).
/// <para>
/// Every test in this class is written against a specific ablation: delete the guard it
/// names in <c>PicoDocument.cs</c> and this test must go red. The mutation ledger lives in
/// <c>.superpowers/sdd/fix-dom/report.md</c>; a test that stays green there is watching
/// something else and does not belong here.
/// </para>
/// </summary>
public class PicoDocumentElementFenceTests
{
    // ── #1 the array indexer stops at the last element ──
    //
    // Without the `if (c < 0) throw` guard the indexer walks NextSibling off the end,
    // returns node index -1, and the caller reads ValueKind == Undefined: a 3-element
    // array silently answers [3] with an empty element instead of failing.

    [Test]
    public void ArrayIndexer_BeyondTheLastElement_ThrowsIndexOutOfRange()
    {
        var doc = PicoDocument.Parse("[1,2,3]"u8.ToArray());
        Assert.Throws<IndexOutOfRangeException>(() => _ = doc.RootElement[3]);
    }

    [Test]
    public void ArrayIndexer_OnAnEmptyArray_ThrowsIndexOutOfRange()
    {
        var doc = PicoDocument.Parse("[]"u8.ToArray());
        Assert.Throws<IndexOutOfRangeException>(() => _ = doc.RootElement[0]);
    }

    // ── #9 a negative index fails like any other out-of-range index ──
    //
    // The walk `for (int i = 0; i < index && c >= 0; i++)` never executes for a negative index,
    // so the cursor stays on the first child: [7,8,9][-1] answers 7 and [-5] answers 7 as well,
    // while [-1] on [] throws. One expression, two behaviours — and the non-empty one is a wrong
    // value that looks right. Fenced to throw, so a negative index reads the same as [3].

    [Test]
    public void ArrayIndexer_WithANegativeIndexOnANonEmptyArray_ThrowsIndexOutOfRange()
    {
        var doc = PicoDocument.Parse("[7,8,9]"u8.ToArray());
        Assert.Throws<IndexOutOfRangeException>(() => _ = doc.RootElement[-1]);
    }

    [Test]
    public void ArrayIndexer_WithANegativeIndexOnAnEmptyArray_ThrowsIndexOutOfRange()
    {
        var doc = PicoDocument.Parse("[]"u8.ToArray());
        Assert.Throws<IndexOutOfRangeException>(() => _ = doc.RootElement[-1]);
    }

    // …and the fence must not have been bought by breaking the indices that do resolve.

    [Test]
    public async Task ArrayIndexer_WithLegalIndexes_StillResolves()
    {
        var doc = PicoDocument.Parse("[7,8,9]"u8.ToArray());
        await Assert.That(doc.RootElement[0].GetInt32()).IsEqualTo(7);
        await Assert.That(doc.RootElement[2].GetInt32()).IsEqualTo(9);
    }

    // ── #2 GetString refuses everything that is not a string ──
    //
    // The string slice of a number node covers its raw token, so dropping the kind guard
    // returns "42" for Parse("42") — a number read as text, with no error anywhere.

    [Test]
    public void GetString_OnANumber_ThrowsInvalidOperation()
    {
        var doc = PicoDocument.Parse("42"u8.ToArray());
        Assert.Throws<InvalidOperationException>(() => _ = doc.RootElement.GetString());
    }

    // ── #3 GetBoolean throws instead of answering "false" ──
    //
    // The `_ => false` mutation turns both of these into a plain false return: a number and
    // a JSON null would then both read as "not true", which is indistinguishable from a
    // genuine `false` member.

    [Test]
    public void GetBoolean_OnANumber_ThrowsInvalidOperation()
    {
        var doc = PicoDocument.Parse("42"u8.ToArray());
        Assert.Throws<InvalidOperationException>(() => _ = doc.RootElement.GetBoolean());
    }

    [Test]
    public void GetBoolean_OnNull_ThrowsInvalidOperation()
    {
        var doc = PicoDocument.Parse("null"u8.ToArray());
        Assert.Throws<InvalidOperationException>(() => _ = doc.RootElement.GetBoolean());
    }

    // ── #4 the array indexer refuses an object ──
    //
    // Without the kind guard the indexer walks the *member list* of an object, so
    // {"a":1,"b":2}[0] returns the value of the first member (1) as if it were an element.

    [Test]
    public void ArrayIndexer_OnAnObject_ThrowsInvalidOperation()
    {
        var doc = PicoDocument.Parse("{\"a\":1,\"b\":2}"u8.ToArray());
        Assert.Throws<InvalidOperationException>(() => _ = doc.RootElement[0]);
    }

    // ── #5 GetArrayLength refuses an object ──
    //
    // Without the kind guard it counts the object's members and reports them as a length:
    // {"a":1,"b":2}.GetArrayLength() == 2.

    [Test]
    public void GetArrayLength_OnAnObject_ThrowsInvalidOperation()
    {
        var doc = PicoDocument.Parse("{\"a\":1,\"b\":2}"u8.ToArray());
        Assert.Throws<InvalidOperationException>(() => _ = doc.RootElement.GetArrayLength());
    }

    // ── #6 TryGet* refuses a string, and leaves `value` at 0 ──
    //
    // The number-kind guard is what stops the span of a string node ("42", quotes stripped)
    // from being parsed as a number. Without it the call answers true/42 — the "silently
    // converted" case, where a schema mismatch never surfaces. `value == 0` is asserted as
    // well: a false answer must not smuggle a half-written out value.

    [Test]
    public async Task TryGetInt32_OnANumericString_ReturnsFalseAndZero()
    {
        var doc = PicoDocument.Parse("\"42\""u8.ToArray());
        var ok = doc.RootElement.TryGetInt32(out var value);
        await Assert.That(ok).IsFalse();
        await Assert.That(value).IsEqualTo(0);
    }

    [Test]
    public async Task TryGetInt64_OnANumericString_ReturnsFalseAndZero()
    {
        var doc = PicoDocument.Parse("\"42\""u8.ToArray());
        var ok = doc.RootElement.TryGetInt64(out var value);
        await Assert.That(ok).IsFalse();
        await Assert.That(value).IsEqualTo(0L);
    }

    [Test]
    public async Task TryGetUInt64_OnANumericString_ReturnsFalseAndZero()
    {
        var doc = PicoDocument.Parse("\"42\""u8.ToArray());
        var ok = doc.RootElement.TryGetUInt64(out var value);
        await Assert.That(ok).IsFalse();
        await Assert.That(value).IsEqualTo(0UL);
    }

    // ── #8 GetInt32 narrows with `checked` ──
    //
    // A number token too wide for int takes the long path; without `checked` the cast wraps,
    // so 4294967297 (2^32 + 1) comes back as 1 — a valid-looking int invented out of a value
    // this type cannot hold.

    [Test]
    public void GetInt32_OnAValueWiderThanInt32_ThrowsOverflow()
    {
        var doc = PicoDocument.Parse("4294967297"u8.ToArray());
        Assert.Throws<OverflowException>(() => _ = doc.RootElement.GetInt32());
    }
}
