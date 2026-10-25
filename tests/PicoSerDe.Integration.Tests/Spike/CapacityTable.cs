// SPIKE — 验证「判定点表」能否表达 §3 与现状表的每一格。
// 这不是产品代码：它是一次性可行性证明，回答六轮复核反复卡住的那个问题：
//   给定 (格式, 形状) 与一张按「判定点」索引的能力表，
//   能否**唯一**算出该格的结论（支持 / 丢成员 / 丢元素），且与实测一致？
// 若能，说明 B1（聚合桶冲突）/ B2（缺行→构造公式）/ B3（粒度未定义）在模型上可解。

using System.Collections.Frozen;

namespace PicoSerDe.Integration.Tests.Spike;

/// <summary>提取期的判定点 —— 从 25 处 formatTag 判据**逐个对号**得来，不是按形状分的。</summary>
internal enum Decision
{
    /// <summary>扩展集合 kind（hashset/queue/stack/…）作为**普通成员**时是否解析。</summary>
    ExtendedKindResolves,
    /// <summary>扩展集合作为**集合元素**时是否被接受（JSON 解析了 kind 但在元素位仍丢弃）。</summary>
    ExtendedKindAsElement,
    /// <summary>仅 json 支持的标量 kind（char/half/bigint/int128/nint/datetimeoffset/uri/version）。</summary>
    ScalarKindResolves,
    /// <summary>byte[] 作为二进制处理（msgpack）。</summary>
    ByteArrayAsBinary,
    /// <summary>嵌套容器，最内层是标量（`List<List<int>>`）。</summary>
    NestedScalarContainer,
    /// <summary>嵌套容器，最内层是对象（`List<List<Dto>>`）。</summary>
    NestedObjectContainer,
    /// <summary>可空**值**叶子（`int?`）在序列/成员内。</summary>
    NullableValueLeaf,
    /// <summary>可空**引用**叶子（`string?`）在序列/成员内。</summary>
    NullableRefLeaf,
    /// <summary>对象列表作为**成员**。</summary>
    ObjectListInMember,
    /// <summary>字典的键 kind 集合。</summary>
    DictKeys,
    /// <summary>字典的**值**是集合。</summary>
    DictValueCollection,
    /// <summary>自递归（`Node.Child : Node`）。</summary>
    RecursiveSelf,
    /// <summary>互递归（`M1.Child : M2`）。</summary>
    RecursiveMutual,
}

internal enum Outcome
{
    /// <summary>正常发射。</summary>
    Supported,
    /// <summary>丢整个成员（诊断）。</summary>
    DropMember,
    /// <summary>只丢该元素/值，成员照发（诊断）。</summary>
    DropElement,
}

/// <summary>每个判定点在某格式上的**效果**（三值）。粒度即效果本身，不再拆两个字段。</summary>
internal readonly record struct DecisionRule(Outcome Effect);

internal static class CapacityTable
{
    // 表格按 (格式, 判定点) 索引。值是**现状**（Spike 的目标是复现现状，不是定终态）。
    private static readonly FrozenDictionary<(string Fmt, Decision D), DecisionRule> Rules =
        new Dictionary<(string, Decision), DecisionRule>
        {
            // 扩展集合：今天只有 json 解析（TypeKindResolver.cs:71）
            { ("json", Decision.ExtendedKindResolves), new(Outcome.Supported) },
            { ("msgpack", Decision.ExtendedKindResolves), new(Outcome.DropMember) },
            { ("toml", Decision.ExtendedKindResolves), new(Outcome.DropMember) },
            { ("yaml", Decision.ExtendedKindResolves), new(Outcome.DropMember) },
            { ("ini", Decision.ExtendedKindResolves), new(Outcome.DropMember) },

            // 嵌套容器 —— **必须分标量/对象两种**：S3 与 S4 在 TOML 结论相反
            { ("json", Decision.NestedScalarContainer), new(Outcome.Supported) },
            { ("msgpack", Decision.NestedScalarContainer), new(Outcome.Supported) },
            { ("toml", Decision.NestedScalarContainer), new(Outcome.Supported) },
            { ("yaml", Decision.NestedScalarContainer), new(Outcome.Supported) },
            { ("ini", Decision.NestedScalarContainer), new(Outcome.DropMember) },

            { ("json", Decision.NestedObjectContainer), new(Outcome.Supported) },
            { ("msgpack", Decision.NestedObjectContainer), new(Outcome.Supported) },
            { ("toml", Decision.NestedObjectContainer), new(Outcome.DropMember) },  // 今天丢（D-3）
            { ("yaml", Decision.NestedObjectContainer), new(Outcome.Supported) },
            { ("ini", Decision.NestedObjectContainer), new(Outcome.DropMember) },

            // 可空值叶子：toml/ini 无 null 表示 ⇒ 丢成员；yaml 今天未实现 ⇒ 也丢成员
            { ("json", Decision.NullableValueLeaf), new(Outcome.Supported) },
            { ("msgpack", Decision.NullableValueLeaf), new(Outcome.Supported) },
            { ("toml", Decision.NullableValueLeaf), new(Outcome.DropMember) },
            { ("yaml", Decision.NullableValueLeaf), new(Outcome.DropMember) },
            { ("ini", Decision.NullableValueLeaf), new(Outcome.DropMember) },

            // 可空引用叶子：toml/ini 无 null ⇒ 丢成员；yaml **发射但丢元素**（D-7b）
            { ("json", Decision.NullableRefLeaf), new(Outcome.Supported) },
            { ("msgpack", Decision.NullableRefLeaf), new(Outcome.Supported) },
            { ("toml", Decision.NullableRefLeaf), new(Outcome.DropElement) },
            { ("yaml", Decision.NullableRefLeaf), new(Outcome.DropElement) },  // 今天的行为
            { ("ini", Decision.NullableRefLeaf), new(Outcome.DropMember) },

            // 扩展集合作**元素**：只有 json 解析了 kind，但元素位仍被提取期丢弃（D-2）；四格式更早丢
            { ("json", Decision.ExtendedKindAsElement), new(Outcome.DropMember) },
            { ("msgpack", Decision.ExtendedKindAsElement), new(Outcome.DropMember) },
            { ("toml", Decision.ExtendedKindAsElement), new(Outcome.DropMember) },
            { ("yaml", Decision.ExtendedKindAsElement), new(Outcome.DropMember) },
            { ("ini", Decision.ExtendedKindAsElement), new(Outcome.DropMember) },

            // 对象列表作成员：ini 不行
            { ("json", Decision.ObjectListInMember), new(Outcome.Supported) },
            { ("msgpack", Decision.ObjectListInMember), new(Outcome.Supported) },
            { ("toml", Decision.ObjectListInMember), new(Outcome.Supported) },
            { ("yaml", Decision.ObjectListInMember), new(Outcome.Supported) },
            { ("ini", Decision.ObjectListInMember), new(Outcome.DropMember) },

            // 递归 —— **必须分自/互**：S11a 在 TOML 被丢，S11b 今天会发射（但有 D-5 覆盖根成员）
            { ("json", Decision.RecursiveSelf), new(Outcome.Supported) },
            { ("msgpack", Decision.RecursiveSelf), new(Outcome.Supported) },
            { ("toml", Decision.RecursiveSelf), new(Outcome.DropMember) },
            { ("yaml", Decision.RecursiveSelf), new(Outcome.DropMember) },
            { ("ini", Decision.RecursiveSelf), new(Outcome.DropMember) },

            { ("json", Decision.RecursiveMutual), new(Outcome.Supported) },
            { ("msgpack", Decision.RecursiveMutual), new(Outcome.Supported) },
            { ("toml", Decision.RecursiveMutual), new(Outcome.Supported) },   // 发射，D-5 另记
            { ("yaml", Decision.RecursiveMutual), new(Outcome.DropMember) },
            { ("ini", Decision.RecursiveMutual), new(Outcome.DropMember) },

            // 字典值集合：五个格式在提取期全丢
            { ("json", Decision.DictValueCollection), new(Outcome.DropMember) },
            { ("msgpack", Decision.DictValueCollection), new(Outcome.DropMember) },
            { ("toml", Decision.DictValueCollection), new(Outcome.DropMember) },
            { ("yaml", Decision.DictValueCollection), new(Outcome.DropMember) },
            { ("ini", Decision.DictValueCollection), new(Outcome.DropMember) },
        }.ToFrozenDictionary();

    public static DecisionRule Rule(string fmt, Decision d) => Rules[(fmt, d)];

    /// <summary>
    /// 形状 → 判定点的**有序**通路。这就是六轮复核里一直缺的「行 → 构造」公式：
    /// 每条通路声明该形状要经过哪些判定点，**短路规则：任一判定点返回非 Supported 即为该格结论**。
    /// 注意：通路里只放**必要条件**；`ArrayOfTables` 之类「某格式的实现方式」不进通路。
    /// </summary>
    public static readonly FrozenDictionary<string, Decision[]> Paths = new Dictionary<string, Decision[]>
    {
        ["S1"] = [],                                                        // 扁平标量列表：无判定点
        ["S2"] = [Decision.ObjectListInMember],
        ["S3"] = [Decision.NestedScalarContainer],
        ["S4"] = [Decision.NestedObjectContainer],
        ["S5"] = [Decision.NullableValueLeaf],
        ["S6"] = [Decision.NestedScalarContainer, Decision.NullableValueLeaf],
        ["S7"] = [Decision.ExtendedKindResolves],
        ["S8"] = [Decision.NestedScalarContainer, Decision.ExtendedKindAsElement],
        ["S9"] = [],                                                        // 扁平字典：无判定点
        ["S10"] = [Decision.DictValueCollection],
        ["S11a"] = [Decision.RecursiveSelf],
        ["S11b"] = [Decision.RecursiveMutual],
        ["S12"] = [Decision.NullableRefLeaf],
        ["S13"] = [Decision.NullableValueLeaf],
    }.ToFrozenDictionary();

    /// <summary>任一判定点短路为结论；全部放行则 Supported。</summary>
    public static Outcome Decide(string fmt, string shape)
    {
        var outcome = Outcome.Supported;
        foreach (var d in Paths[shape])
        {
            var e = Rule(fmt, d).Effect;
            if (e == Outcome.Supported) continue;
            // 通路取**最严**效果：丢成员优先于丢元素（丢成员时不可能再"只剩丢元素"）。
            if (e == Outcome.DropMember) return Outcome.DropMember;
            outcome = Outcome.DropElement;
        }
        return outcome;
    }
}
