// SPIKE 的判定部分：把 §3.1 现状表的 70 格（14 形状 × 5 格式）逐格与模型的推算比对。
// 若全绿 ⇒ 「判定点表 + 通路公式」足以表达现状 ⇒ B1/B2/B3 在模型层可解。
// 若某格红 ⇒ 说明该判定点仍需拆分（这正是本 spike 要暴露的东西）。

namespace PicoSerDe.Integration.Tests.Spike;

public class CapacityTableSpikeTests
{
    // 实测现状（spec v7 §3.1，基线 84a9560）。Supported=✅，DropMember=成员被丢弃(⛔/⏸)，
    // DropElement=成员在但元素/值丢失(🐞)。
    private static readonly (string Shape, string Fmt, Outcome Expected)[] Observed =
    [
        ("S1",  "json", Outcome.Supported), ("S1",  "msgpack", Outcome.Supported), ("S1",  "toml", Outcome.Supported), ("S1",  "yaml", Outcome.Supported), ("S1",  "ini", Outcome.Supported),

        ("S2",  "json", Outcome.Supported), ("S2",  "msgpack", Outcome.Supported), ("S2",  "toml", Outcome.Supported), ("S2",  "yaml", Outcome.Supported), ("S2",  "ini", Outcome.DropMember),

        ("S3",  "json", Outcome.Supported), ("S3",  "msgpack", Outcome.Supported), ("S3",  "toml", Outcome.Supported), ("S3",  "yaml", Outcome.Supported), ("S3",  "ini", Outcome.DropMember),

        ("S4",  "json", Outcome.Supported), ("S4",  "msgpack", Outcome.Supported), ("S4",  "toml", Outcome.DropMember), ("S4",  "yaml", Outcome.Supported), ("S4",  "ini", Outcome.DropMember),

        ("S5",  "json", Outcome.Supported), ("S5",  "msgpack", Outcome.Supported), ("S5",  "toml", Outcome.DropMember), ("S5",  "yaml", Outcome.DropMember), ("S5",  "ini", Outcome.DropMember),

        ("S6",  "json", Outcome.Supported), ("S6",  "msgpack", Outcome.Supported), ("S6",  "toml", Outcome.DropMember), ("S6",  "yaml", Outcome.DropMember), ("S6",  "ini", Outcome.DropMember),

        ("S7",  "json", Outcome.Supported), ("S7",  "msgpack", Outcome.DropMember), ("S7",  "toml", Outcome.DropMember), ("S7",  "yaml", Outcome.DropMember), ("S7",  "ini", Outcome.DropMember),

        ("S8",  "json", Outcome.DropMember), ("S8",  "msgpack", Outcome.DropMember), ("S8",  "toml", Outcome.DropMember), ("S8",  "yaml", Outcome.DropMember), ("S8",  "ini", Outcome.DropMember),

        ("S9",  "json", Outcome.Supported), ("S9",  "msgpack", Outcome.Supported), ("S9",  "toml", Outcome.Supported), ("S9",  "yaml", Outcome.Supported), ("S9",  "ini", Outcome.Supported),

        ("S10", "json", Outcome.DropMember), ("S10", "msgpack", Outcome.DropMember), ("S10", "toml", Outcome.DropMember), ("S10", "yaml", Outcome.DropMember), ("S10", "ini", Outcome.DropMember),

        ("S11a","json", Outcome.Supported), ("S11a","msgpack", Outcome.Supported), ("S11a","toml", Outcome.DropMember), ("S11a","yaml", Outcome.DropMember), ("S11a","ini", Outcome.DropMember),

        ("S11b","json", Outcome.Supported), ("S11b","msgpack", Outcome.Supported), ("S11b","toml", Outcome.Supported), ("S11b","yaml", Outcome.DropMember), ("S11b","ini", Outcome.DropMember),

        ("S12", "json", Outcome.Supported), ("S12", "msgpack", Outcome.Supported), ("S12", "toml", Outcome.DropElement), ("S12", "yaml", Outcome.DropElement), ("S12", "ini", Outcome.DropMember),

        ("S13", "json", Outcome.Supported), ("S13", "msgpack", Outcome.Supported), ("S13", "toml", Outcome.DropMember), ("S13", "yaml", Outcome.DropMember), ("S13", "ini", Outcome.DropMember),
    ];

    [Test]
    public async Task Model_reproduces_all_70_observed_cells()
    {
        var wrong = Observed
            .Select(o => (o, actual: CapacityTable.Decide(o.Fmt, o.Shape)))
            .Where(x => x.actual != x.o.Expected)
            .Select(x => $"{x.o.Shape}x{x.o.Fmt}: expected {x.o.Expected}, model said {x.actual}")
            .ToArray();

        await Assert.That(wrong).IsEmpty().Because(string.Join(" | ", wrong));
    }

    [Test]
    public async Task Every_shape_has_a_decision_path()
    {
        foreach (var shape in new[] { "S1","S2","S3","S4","S5","S6","S7","S8","S9","S10","S11a","S11b","S12","S13" })
            await Assert.That(CapacityTable.Paths.ContainsKey(shape)).IsTrue().Because(shape);
    }

    /// <summary>I-7：每处不支持都能回答丢弃粒度 —— 不能有哪个判定点在被拦时没有粒度。</summary>
    [Test]
    public async Task Every_blocked_decision_has_a_granularity()
    {
        foreach (var shape in CapacityTable.Paths.Keys)
        foreach (var fmt in new[] { "json","msgpack","toml","yaml","ini" })
        {
            var actual = CapacityTable.Decide(fmt, shape);
            await Assert.That(actual is Outcome.Supported or Outcome.DropMember or Outcome.DropElement)
                .IsTrue().Because($"{shape}x{fmt} must resolve to a granularity, got {actual}");
        }
    }
}
