namespace PicoIni.Tests;

/// <summary>
/// C-18/C-20 follow-up coverage (review I-2 / I-3): the inline-comment contract
/// has two directions and every fail-loud parse branch needs a red-proof test,
/// including the non-final (streaming) quote path that no test reached before.
/// </summary>
public class IniStrictnessGapTests
{
    // ── I-2: ';' / '#' after whitespace still opens a comment ──

    [Test]
    [Arguments("A = v ; note")]
    [Arguments("A = v # note")]
    [Arguments("A = v\t; note")]
    public async Task CommentAfterWhitespace_IsStripped(string text) =>
        await Assert
            .That(IniSerializer.Deserialize<TextDto>(Encoding.UTF8.GetBytes(text))!.A)
            .IsEqualTo("v");

    /// <summary>
    /// A value that is only a comment is an <em>empty</em> value. Since C-30 an
    /// empty unquoted value is the null literal, so this member is null rather
    /// than the old empty string (a quoted <c>A = ""</c> still yields "").
    /// </summary>
    [Test]
    public async Task ValueIsOnlyAComment_IsNull() =>
        await Assert
            .That(IniSerializer.Deserialize<TextDto>(Encoding.UTF8.GetBytes("A = ;onlycomment"))!.A)
            .IsNull();

    [Test]
    public async Task QuotedEmptyValue_IsEmptyString() =>
        await Assert
            .That(IniSerializer.Deserialize<TextDto>(Encoding.UTF8.GetBytes("A = \"\""))!.A)
            .IsEqualTo("");

    [Test]
    public async Task HashDirectlyAfterValue_IsPreserved() =>
        await Assert
            .That(IniSerializer.Deserialize<TextDto>(Encoding.UTF8.GetBytes("A = a#b"))!.A)
            .IsEqualTo("a#b");

    // ── I-3: the remaining fail-loud branches ──

    [Test]
    public void InvalidDateTime_Throws() =>
        Assert.Throws<FormatException>(() =>
            IniSerializer.Deserialize<DateTimeDto>(Encoding.UTF8.GetBytes("Moment = not-a-date"))
        );

    [Test]
    public void InvalidTimeSpan_Throws() =>
        Assert.Throws<FormatException>(() =>
            IniSerializer.Deserialize<TimeSpanDto>(Encoding.UTF8.GetBytes("Span = not-a-span"))
        );

    [Test]
    public void InvalidDateTimeFormat_Throws() =>
        Assert.Throws<FormatException>(() =>
            IniSerializer.Deserialize<IniDateTimeFormatPoco>(
                Encoding.UTF8.GetBytes("Date = 23/10/2026")
            )
        );

    [Test]
    public async Task InvalidDateTimeFormat_ValidValue_StillParses() =>
        await Assert
            .That(
                IniSerializer
                    .Deserialize<IniDateTimeFormatPoco>(
                        Encoding.UTF8.GetBytes("Date = 2026-10-23")
                    )!
                    .Date
            )
            .IsEqualTo(new DateTime(2026, 10, 23));

    // ── I-3: non-final block must ask for more data, never throw ──

    [Test]
    public async Task UnterminatedQuote_NonFinalBlock_NeedsMoreData()
    {
        bool result,
            needsMore;
        {
            // Unterminated quote: the reader cannot tell truncation from an
            // incomplete chunk, so a non-final block reports NeedsMoreData.
            var r = new IniReader("A = \"oops"u8, isFinalBlock: false);
            r.Read();
            result = r.Read();
            needsMore = r.NeedsMoreData;
        }
        await Assert.That(result).IsFalse();
        await Assert.That(needsMore).IsTrue();
    }

    [Test]
    public async Task UnterminatedQuote_NonFinalBlock_ResumesWhenCompleted()
    {
        // The whole point of NeedsMoreData: appending the rest must succeed.
        bool firstRead;
        {
            var first = new IniReader("A = \"oops"u8, isFinalBlock: false);
            first.Read();
            firstRead = first.Read();
        }
        await Assert.That(firstRead).IsFalse();

        string value;
        {
            var second = new IniReader("A = \"oops done\""u8, isFinalBlock: true);
            second.Read();
            second.Read();
            value = Encoding.UTF8.GetString(second.GetStringRaw());
        }
        await Assert.That(value).IsEqualTo("oops done");
    }
}

public class DateTimeDto
{
    public DateTime Moment { get; set; }
}

public class TimeSpanDto
{
    public TimeSpan Span { get; set; }
}
