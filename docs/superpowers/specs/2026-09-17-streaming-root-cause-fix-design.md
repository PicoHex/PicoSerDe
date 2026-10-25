# 流式解析与生成器根因修复 — 设计规格

- **日期**: 2026-09-17
- **分支**: `fix/streaming-root-cause`
- **依据**: 2026-09-17 代码审核与复核报告（P0-1…P0-5、P1-6…P1-7）
- **目标读者**: 实施者（无本仓库上下文也可执行）；实现细节见配套实施计划

---

## 1. 问题陈述

审核与复核确认了 5 个 P0 级缺陷（全部在仓库自有测试工程中复现）：

| 编号 | 缺陷 | 证据 |
|---|---|---|
| P0-1 | 自引用/递归 DTO 触发源生成器**栈溢出**，整个编译失败 | `TreeNode { TreeNode? Child }` → `error : Stack overflow.`，`Repeated 310 times: ExtractProperties/ExtractNestedProperties` |
| P0-2 | JSON `DeserializeFromStreamAsync` 在真实分块流上**静默丢数据**；顶层对象数组**死循环** | 真实 `FileStream` 165 KB → 2000 行仅 94 行，无异常；`Elem[]` @8B 分块挂死 |
| P0-3 | INI `DeserializeFromStreamAsync` **恒返回 null** | 单块 `MemoryStream` 即复现 |
| P0-4 | TOML/YAML/INI 流式读 >4KB payload 崩溃或静默部分数据 | `IniReader.ReadKeyValueSeq():603` 等；TOML 返回 `0/0/null` |
| P0-5 | `DeserializeAsyncEnumerable(topLevelValues:false)` 任意分块均失败 | `[1,2]` @5B 分块 → `FormatException @JsonSerializer.cs:317` |

另含 P1-6（BOM 不一致：JSON 抛错、INI/TOML/YAML 静默丢首键）与 P1-7（TOML/YAML 流式忽略嵌套对象）。

## 2. 根因分析

1. **流式是"第二套实现"**：5 个格式各有一套与同步路径平行的解析与生成代码；同步路径（span、整文档）测试充分，流式路径（seq、分块）只在单块 `MemoryStream` + 标量 DTO 上测过。
2. **seq 读取器没有 chunk 边界契约**：token 未完成时要么静默失败（JSON 集合内层循环吞掉 `NeedsMoreData`），要么越界/错位（TOML/YAML/INI 的 `*Seq` 扫描）。
3. **生成的流式委托没有 partial 状态与嵌套支持**：INI/TOML/YAML 的流式委托每次 `new T()`、EOF 不赋值、对 object/dict 直接 `Skip()`。
4. **生成器的嵌套属性提取没有环检测**：`ExtractNestedProperties → ExtractProperties → ExtractNestedProperties` 无界递归。

**结论：必须让流式路径与同步路径共享同一套解析状态机，并把"边界未完成 → 原地回退到 token 起点"写进 reader 契约。**

## 3. 目标与非目标

**目标**
- G1：递归/自引用类型可编译，并支持序列化/反序列化（类型级递归，任意深度由 `MaxDepth` 约束）。
- G2：JSON 流式在所有分块边界下与同步路径结果一致，无静默丢数据、无死循环。
- G3：TOML/YAML/INI 流式在所有分块边界下与同步路径结果一致（含嵌套对象/集合）。
- G4：`DeserializeAsyncEnumerable` 数组模式在所有分块边界下正确，内存有界（消费即压缩）。
- G5：5 个格式 BOM 行为一致（UTF-8 BOM 可跳过；仅 BOM 输入明确报错）。
- G6：新增 chunk 契约测试矩阵进入仓库测试套件；现有 1330 测试保持全绿、0 warning。

**非目标（本轮不做，另立计划）**
- 性能优化（`PropertyNamingPolicy` 缓存、`ReadNumberSeq` 之外的分配优化）。
- `MaxDepth` 读写语义拆分、流式 API 签名统一等破坏性 API 变更。
- 文档站/README 大改（代码落地后同步最小修正）。

## 4. 总体设计

### WP1 — JSON 流式根因修复（G2）

**核心机制：生成代码级快照/回退 + 内层状态传播**，不做全缓冲。

1. **Reader 新增最小公共 API**（`PicoJetson.JsonReader`）：
   - `long BytesConsumed`（已存在，span 模式=`_position`，seq 模式=已消费字节数）；
   - `void RewindTo(long consumedOffset)`：span 模式设置 `_position`；seq 模式 `_seqReader.Rewind(_seqReader.Consumed - offset)`。
   - 语义：把读取位置退回同一缓冲区内的早先偏移；仅在 token 未完成、需要重读时调用。
2. **生成的流式委托在"可重读单元"入口取快照**：
   - 对象属性：外层循环每轮在读取属性名**之前** `var __snap = reader.BytesConsumed;`；
   - 顶层数组元素：读元素前取快照；
   - 任何内层读取（集合元素循环、嵌套对象 helper 调用、字典值、嵌套列表）结束后：
     `if (reader.NeedsMoreData) { reader.RewindTo(__snap); return ReadStatus.NeedMoreData; }`
   - 恢复时同一单元被完整重读；集合/对象在单元内先赋值后填充，天然幂等。
3. **顶层对象数组**：部分结果保存在 `partial`/`StreamState`（与手写 `IntArrayFunc` 同构），失败单元回退到元素起点。
4. **嵌套对象 helper 不改签名**：调用点负责检查 `NeedsMoreData` 并回退到属性快照（helper 返回的 partial 对象被丢弃并在恢复后重建）。
5. **不变量**：任何 `return ReadStatus.NeedMoreData` 之前，读取位置必须已回退到"下一次重读的起点"；任何 `ReadStatus.Success` 返回的 `partial` 必须与同步路径逐字段一致。

### WP2 — TOML/YAML/INI 流式（G3）

**实施结果（本分支，已完成）**：

1. **Core 提炼（共享）**：新增 `PicoSerDe.Core.ITransactionalTokenReader`（`Mark()`/`RewindToMark()`/`TokenStart`/`BytesConsumed`/`RewindTo`），文档化“成员级可重入 + Read 完成后状态一致”的契约；`IncompleteInputException` 上移到 Core，经 `InternalsVisibleTo` 由 5 个格式程序集共享（无公开 API 扩散）。JSON/TOML/YAML/INI 四个文本 reader 均实现该契约。
2. **显式 incomplete 信号（已完成）**：三个 reader 的所有“扫描到达缓冲区末尾”路径改抛共享信号；TOML 多行数组/内联表原子读取；YAML 缩进栈随 `Mark()` 快照；INI section 名随导出的 state 传递，恢复时重发 ObjectStart（不重复计深度）。
3. **真增量流式（已完成并验证）**：TOML/YAML/INI 生成器均注册 partial 委托，统一模式：成员名读取前 `Mark()`、成员内失败 `RewindToMark()`、读下一 token 失败不回退（状态已一致）。`StreamingContractTests` 三格式 × chunk{1,2,3,8,4096} × 嵌套对象/集合/嵌套 section 全部通过。
4. 过程中的额外修复：状态构造器 `MaxDepth` 默认值（0 会误触深度上限）、INI 列表重读重置、`StreamingRunner` 诊断信息、生成的 YAML 委托 CS0162 抑制。

### WP3 — `DeserializeAsyncEnumerable` 数组模式（G4）

- 删除手写 bracket-counting 解析器；
- 实现"累积缓冲 + `JsonReader` span 切片"：
  - 维护 `byte[] buffer` + `head/consumed`，追加读取数据；
  - 从 `buffer[consumed..count]` 建 `JsonReader`（最终块标志在流结束时置 true），读元素 token → `RawPos` 记起点 → `TrySkip()` 消费元素 → 切原始字节 → 调 `DesRegistry<T>.Deserializer`；
  - 元素不完整：等待更多数据（同一位置重试）；消费后按阈值压缩缓冲；
  - 保持惰性 `IAsyncEnumerable<T?>` 语义与"需注册元素反序列化器"契约。

### WP4 — 递归类型支持（G1）

**实施结果（本分支）**：

1. **环检测（已落地）**：`ExtractNestedPropertiesGuarded` 携带提取栈（`SymbolEqualityComparer.Default`），检测到环时给 `PropertyInfo.IsRecursiveRef` 打标，不再递归——生成器不再栈溢出。
2. **JSON/MsgPack 完整递归（已落地）**：`GenerateAll` 用 cycle target 的真实顶层属性集 seed `nestedTypes`，inner helper 自/互引用；序列化与反序列化都支持（测试：自递归深度 3、互递归、数据级环 fail-loud）。
3. **TOML/YAML/INI（已落地）**：这三种格式无法表达任意深度嵌套，递归成员在提取阶段被跳过（保持既有嵌套限制；后续可加构建诊断或运行时 fail-loud）。
4. 覆盖：`RecursiveTypeTests`（4 例，跨格式工程）。

### WP5 — BOM 与廉价 P2（G5 + 卫生）

1. **BOM**：JSON/TOML/YAML/INI 的 span 构造器跳过 `EF BB BF`；seq 模式在首次 `Read()` 检查（不足 3 字节且非最终块 → NeedMoreData；恢复的 reader 不重复检查）。**仅 BOM 输入 ≡ 空输入**（各格式既有语义：JSON 抛 `FormatException`，TOML/YAML/INI 返回空对象）。MsgPack 为二进制，不处理。
2. 删除 `JsonOptions.cs` 悬空的 `[ThreadStatic] Current` 注释。
3. `GenInfrastructure.IsCandidate` 补 `SerializeLines/DeserializeLines/DeserializeFromStreamAsync/DeserializeAsyncEnumerable`，并在 `Transform` 中提取泛型类型参数。
4. BUG-11：`JsonReader.ReadNumberSeq` 缓冲按需增长（不再 `Rent(32)` 越界）。

## 5. 接口契约（新增部分）

```csharp
// PicoJetson.JsonReader
public long BytesConsumed { get; }              // 已存在
public void RewindTo(long consumedOffset);      // 新增：同一缓冲区内回退

// 生成代码（流式委托）— 约定
long __snap = reader.BytesConsumed;             // 重读单元入口
// ... 单元读取 ...
if (reader.NeedsMoreData) { reader.RewindTo(__snap); return ReadStatus.NeedMoreData; }
```

```csharp
// PicoSerDe.Core（WP2，Plan 2）
internal sealed class ChunkedInputBuffer { /* 未消费字节 + 压缩 + 追加 */ }
// reader 契约：token 未完成 → 位置保持在 token 起点 + NeedsMoreData
```

## 6. 错误处理

- 严格语义不变：`FormatException` 是唯一解析错误类型（BOM、截断、越界统一转 `FormatException`）。
- 流式与同步路径对同一输入必须给出**相同结果或相同异常类型**。
- 任何"未实现/不支持"分支必须 fail-loud（不得静默丢字段）；不再允许 `Skip()` 静默跳过嵌套对象。

## 7. 测试策略

1. **chunk 契约矩阵**（新增，跨格式）：chunk ∈ {1,2,3,5,8,16,4096} × payload ∈ {标量、`List<T>`、`T[]`、`Dictionary<K,V>`、嵌套对象（≥2 层）、对象列表、嵌套列表、含转义/多字节 UTF-8}；断言 `stream(chunked) == sync`，逐字段比较。
2. **防挂死**：涉及数组模式与顶层数组的测试必须带超时（TUnit `[Timeout]`），防止回归为死循环。
3. **真实流**：至少一条测试用真实 `FileStream`（≥100 KB）验证 P0-2。
4. **回归**：现有 1330 测试全绿；`tests/PicoSerDe.Integration.Tests` 的 chunk 矩阵进 CI。
5. **生成器**：递归 DTO 的测试文件常驻仓库（编译通过即验证 P0-1）；round-trip 断言验证运行时支持。

## 8. 风险与缓解

| 风险 | 缓解 |
|---|---|
| 生成器改动面大（6k 行文件、多发射点） | 先加失败测试锁定行为；只改流式发射路径；每个语义点单独测试 |
| 快照/回退遗漏某个发射点 | chunk 矩阵覆盖所有属性类型；真实流测试；代码审查逐点核对 `NeedMoreData` |
| WP2 重写 seq 解析引入新越界 | span 单解析器 + 显式 incomplete 信号；删除 `*Seq` 减少代码面；矩阵测试 |
| WP4 互相引用 helper 命名冲突 | 复用现有 `SafeName` 命名与 `typeMap`；编译期测试覆盖 A→B→A |
| 大 token 内存上界 | 记录在案：上界=最大未完成 token；后续可加配置上限 |

## 9. 交付状态

- **WP1（JSON 流式）：完成**；**WP3（数组模式）：完成**；**WP4（递归类型）：完成（JSON/MsgPack 递归，其余三格式跳过）**；**WP5（BOM/小项）：完成**。
- **WP2（TOML/YAML/INI 流式）：完成** —— reader 归一化 + Core 共享的事务性契约 + 显式 incomplete 信号 + **三格式真增量流式**（含嵌套 section），契约矩阵全绿。
- 验证：全量测试 1403 通过 / 0 失败；`dotnet build PicoSerDe.slnx` 0 warning / 0 error。
