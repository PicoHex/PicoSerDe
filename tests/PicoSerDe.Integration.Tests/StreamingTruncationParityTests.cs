using System.Text;

namespace PicoSerDe.Integration.Tests;

public class TruncParityDoc
{
    public string Scalar { get; set; } = "";
    public int Num { get; set; }
}

/// <summary>
/// Truncated/malformed chunks must fail the same way in the streaming and the
/// synchronous path — streaming must never return a partially filled object
/// (the empty-stream shortcut returns an empty object only for a truly empty
/// document).
/// </summary>
public class StreamingTruncationParityTests
{
    private static string Shape(TruncParityDoc? d) =>
        d is null ? "<throw>" : $"Scalar='{d.Scalar}' Num={d.Num}";

    public static IEnumerable<(string Format, string Text)> Cases() =>
        [
            ("toml-complete", "Scalar = \"x\"\nNum = 1\n"),
            ("toml-truncated-int", "Scalar = \"x\"\nNum = "),
            ("toml-unterminated-string", "Scalar = \"unterminated"),
            ("toml-truncated-section", "[sec]\nScalar = \"x\"\n"),
            ("toml-bad-line", "Scalar = \"x\"\ngarbage without equals\n"),
            ("toml-unterminated-section", "Scalar = \"x\"\n[unterminated"),
            ("yaml-complete", "Scalar: x\nNum: 1\n"),
            ("yaml-unterminated-string", "Scalar: unterminated"),
            ("yaml-bad-indent", "Scalar: x\n\tbad: ["),
            ("yaml-unterminated-flow", "Scalar: [1, 2"),
            ("ini-complete", "Scalar = x\nNum = 1\n"),
            ("ini-empty-value", "Scalar = \n"),
            ("ini-bad-line", "Scalar = x\nbad_line_no_equals\n"),
            ("ini-unterminated-section", "[sec\nScalar = x\n"),
        ];

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task Streaming_MatchesSync_ForTomlYamlIni(string format, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var sync = Capture(() =>
            format switch
            {
                "toml" => (object?)TomlSerializer.Deserialize<TruncParityDoc>(bytes),
                "yaml" => YamlSerializer.Deserialize<TruncParityDoc>(bytes),
                "ini" => IniSerializer.Deserialize<TruncParityDoc>(bytes),
                _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
            }
        );
        var stream = await CaptureStream(() =>
            format switch
            {
                "toml" => AsTask(
                    TomlSerializer.DeserializeFromStreamAsync<TruncParityDoc>(
                        new MemoryStream(bytes)
                    )
                ),
                "yaml" => AsTask(
                    YamlSerializer.DeserializeFromStreamAsync<TruncParityDoc>(
                        new MemoryStream(bytes)
                    )
                ),
                "ini" => AsTask(
                    IniSerializer.DeserializeFromStreamAsync<TruncParityDoc>(
                        new MemoryStream(bytes)
                    )
                ),
                _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
            }
        );

        await Assert
            .That(Shape(stream.Item1) + "|" + (stream.Item1 == null))
            .IsEqualTo(Shape(sync.Item1) + "|" + (sync.Item1 == null))
            .Because($"{format}: sync={sync.Item2 ?? "ok"} stream={stream.Item2 ?? "ok"}");
    }

    private static async Task<object?> AsTask<T>(ValueTask<T> value) => await value;

    private static (TruncParityDoc?, string?) Capture(Func<object?> f)
    {
        try
        {
            return ((TruncParityDoc?)f(), null);
        }
        catch (Exception ex)
        {
            return (null, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static async Task<(TruncParityDoc?, string?)> CaptureStream(Func<Task<object?>> f)
    {
        try
        {
            return ((TruncParityDoc?)await f(), null);
        }
        catch (Exception ex)
        {
            return (null, ex.GetType().Name + ": " + ex.Message);
        }
    }
}
