using System.Globalization;

namespace PicoJetson.Tests;

/// <summary>
/// The DOM layer's number readers must read a JSON number the way JSON spells it, whatever
/// the ambient culture says. JSON (RFC 8259 §6) uses '.' as the decimal point and no digit
/// grouping; under de-DE the frame's own separator is ',', so a culture-sensitive parse reads
/// "3.14" as three hundred and fourteen.
/// <para>
/// The third ablation removed <c>InvariantCulture</c> from <see cref="PicoElement.GetDouble"/>
/// and de-DE reported 314 for a document that says 3.14 — silently, on a machine where the
/// default parse succeeds. This test is the fence: restore the culture check and the
/// mutation turns it red.
/// </para>
/// <para>
/// Culture is process-wide mutable state and TUnit runs tests in parallel, so the class opts
/// out with <see cref="NotInParallelAttribute"/> and the swap is undone in <c>finally</c> —
/// a culture left behind would poison every test that runs after it.
/// </para>
/// </summary>
[NotInParallel]
public class PicoDocumentCultureFenceTests
{
    [Test]
    public async Task GetDouble_UnderDeDe_ReadsTheInvariantDecimalPoint()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            var doc = PicoDocument.Parse("3.14"u8.ToArray());
            await Assert.That(doc.RootElement.GetDouble()).IsEqualTo(3.14);
        }
        finally
        {
            CultureInfo.CurrentCulture = old;
        }
    }
}
