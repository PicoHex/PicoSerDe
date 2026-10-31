# 融合读发射登记表（fused-emission registry）

**这份文件由一条测试强制执行，不是说明文档。**
`tests/PicoSerDe.Integration.Tests/FusedMemberEmissionGuardTests.cs` 读它，并对
`PicoSerDe.Core/src/ITokenReader.cs` 里 `IFusedTokenReader` 声明的**每一个成员** × 每一个**已经追求过发射的格式**
断言：

> 该成员**要么**在该格式的生成基线（`tests/FormatCapabilities.Probe/generated-baseline/<格式>.Gen/`）里被
> **调用**，**要么**在下表里登记为 `不用` **并给出理由**。

## 为什么要有它

这一轮出过一次事故：**T11（MsgPack E-3 融合元素读）从未执行，却没有任何地方说它没做**。
135 条等价性护栏证明的是"成员**正确**"，没有一条证明"成员**被用**"。于是"实现了、但没人调用、也没人说"
这种状态可以一直存在下去 —— 它不会被任何测试发现，只会在下一次有人读计划时被误认为"已完成"。

这条护栏把那个状态变成**红的**：新增一个融合成员（或在某个格式开始发射它）而不同时更新这张表 ⇒ 测试失败，
失败信息里带本文件的路径和该补的那一行。

## 怎么登记（新增一行）

表格的列固定为 **`格式 | 成员 | 状态 | 理由 / 证据`**：

- **格式**：`MsgPack` 或 `JSON`（只登记"追求过发射"的两个格式：T6–T11 是 MsgPack，T14/T15 是 JSON）。
- **成员**：`IFusedTokenReader` 上的方法名，**逐字**（测试按名字比对；拼错等于没登记，另有一条测试会红）。
- **状态**：只有两个值 ——
  - `已发射`：生成基线里**必须**真有 `成员名(` 的调用（否则红：登记表在说谎）；
  - `不用`：实现了但生成器不调用它。**理由列必填**（写清"为什么不发"，不是"没时间"）。
- **理由 / 证据**：可核对的判据（grep 判据 / 符号名 / commit / 数字）。**一句话就够，但必须能被查。**
- **引代码就引符号名，不引行号。** 要指 `JsonReader` 的某个成员，写 `` `JsonReader.TryReadInt32` ``，不写
  行号：成员名用一条 `grep -n 'bool TryReadInt32' PicoJetson/src/PicoJetson/JsonReader.cs`
  就能查到，而行号会被任何一次插入改掉 —— 本表的行号曾因 `JsonReader.cs` 一次 +33 行整体失效（本表五处
  + `api/PENDING-CHANGES.md` 两处，没有任何测试因此变红）。`DocPointerGuardTests` 现在会拒绝这两个
  账本里的行号引用。
- 已发射的成员**可以不登记**（被调用本身就满足规则）；但**一旦登记为 `不用`，就不许在基线里出现** ——
  两边的状态必须一致。想让测试变绿，改表或改发射器，**不许改测试**。

如果某个格式开始发射融合读：把该格式加进测试里的 `Formats` 表，并为它的每个成员在下面补齐一行。

## 登记表

| 格式 | 成员 | 状态 | 理由 / 证据 |
|---|---|---|---|
| MsgPack | TryGetCollectionCount | 已发射 | T9（`9ab02c5`）的 E-1 计数循环：MsgPack 生成基线里 `TryGetCollectionCount(` 出现 **8** 次。MsgPack 的 wire 头带元素个数（`MsgPackReader.HasCountedCollections` = `true`），所以计数循环是它独占的能力 |
| MsgPack | TryReadInt32 | 不用 | 成员在（`MsgPackReader.cs`，T6 `0b5e16e`），但**生成器不发射**：MsgPack 生成基线里 `TryReadInt32(` = **0** 次。**T11（E-3 发射）从未做**，且两轮实验判否 —— 属性站点融合实验 `feat/mp-prop-fused` 在**真实发射**上测出无改善、AOT 慢 4–6 %（计划 §7.2）。回退它不影响任何一行 E2E，只会删掉契约与 135 条护栏 |
| MsgPack | TryReadInt64 | 不用 | 成员在（T7 `76378fd`），无发射点：基线 `TryReadInt64(` = **0**。这是"**已实现但当前无调用方**"，不是"没实现"；发射被否的理由同上一行（计划 §7.2） |
| MsgPack | TryReadFloat64 | 不用 | 成员在（T8 `76378fd`），无发射点：基线 `TryReadFloat64(` = **0**。理由同 `TryReadInt32` 行 |
| MsgPack | TryReadBool | 不用 | 成员在（T8 `76378fd`），无发射点：基线 `TryReadBool(` = **0**。理由同 `TryReadInt32` 行 |
| JSON | TryGetCollectionCount | 不用 | **结构上不可能**：JSON 的 wire 不携带元素个数，`JsonReader.HasCountedCollections` = `false`，契约要求此时恒返回 `false` ⇒ 生成器只能走 `Read()` + 结束符循环 |
| JSON | TryReadInt32 | 已发射 | T15（`727f2ef`）的 E-3：JSON 生成基线里 `TryReadInt32(` 出现 **7** 次（7 个文件的 `List<int>` 元素循环）。它是 JSON 唯一被**覆写**的融合成员（`JsonReader.TryReadInt32`），也是全仓库唯一真的在生成代码里调融合读的地方 |
| JSON | TryReadInt64 | 不用 | `JsonReader.TryReadInt64` 是一行转发（没有 JSON 专用正文），生成器也没有 int64 发射点：基线 `TryReadInt64(` = **0**。T14/T15 的范围只有 int32，int64 站点没做过端到端测量 ⇒ 没有据以发射的数字 |
| JSON | TryReadFloat64 | 不用 | `JsonReader.TryReadFloat64` 一行转发，无发射点：基线 `TryReadFloat64(` = **0**。理由同上 |
| JSON | TryReadBool | 不用 | `JsonReader.TryReadBool` 一行转发，无发射点：基线 `TryReadBool(` = **0**。理由同上 |

## 相关

- 计划：`docs/superpowers/plans/2026-10-28-fused-token-reader.md` §7（每个任务的状态 + 证据指针）、§7.2（T11 为什么没做）
- 本轮总结：`docs/superpowers/specs/2026-10-28-perf-round-summary.md` §12.1（属性站点融合：两轮相反，判否）
- 契约：`PicoSerDe.Core/src/ITokenReader.cs`（`IFusedTokenReader` / `TokenReaderFused`）
