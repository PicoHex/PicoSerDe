# R2 — Capability Landing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** R2 把 24 个「终态要求 ✅ 但今天不是 OK」的格变成 OK，并把三处会让矩阵说假话的机制修好。

**Architecture:** R1 已建立单一能力模型（`shared/Gen/FormatCapabilities.cs` + `DecisionTables.cs`，`Rules` = 现状、`TargetRules` = 终态）与 70 格探针 + 92 文件生成代码基线。R2 不改模型结构，只让**代码追上表**：按根因分组修生成器/抽取管线，每修一组就把对应的 `Rules` 格翻成 `TargetRules` 的值，并用「表说 Supported ⇒ 生成代码必须含该成员」的守卫防止假绿。

**Tech Stack:** .NET 10、C# 源生成器（Roslyn incremental generator）、TUnit 测试、`dotnet build -m:1`、AOT publish。

**Spec:** `docs/superpowers/specs/2026-10-25-format-capabilities-design.md`（尤其 §3 事实表、§6 终态矩阵、§7 不变式、§5.6 三项前置义务）

## Global Constraints

- **R2 是 `x+1`**：wire 输出**允许变化**，但**只允许向 §6 终态方向变**。任何「今天 OK 的格变成 MISMATCH」都是回归。
- **两张表保持 `internal`**：`api/*.public.txt` 在整个 R2 内**零 diff**（D5）。
- **单一能力来源**（I-3）：格式字面量不得出现在判定逻辑里；例外只有 `TypeKindResolver` 的 `byte[]→bytes`（编码选择），且它已带 `// encoding, not capability` 标记。
- **每个任务必须说明它翻哪些格**，并在 PR 描述里逐格列出「格式 / 形状 / 前 / 后」。
- **禁止"只翻表"**：任何把 `Rules` 格改成 `Supported` 的改动，必须伴随生成器改动 **且** 由探针实测该格 OK。守卫测试会抓这种假绿。
- 构建一律 `dotnet build PicoSerDe.slnx -c Debug -m:1`，要求 **0 warning / 0 error**；测试 `dotnet test PicoSerDe.slnx -c Debug --no-progress` 要求 **0 failed**。
- 提交前 `dotnet csharpier format <改动的 .cs>`；**不要**在业务提交里夹带全仓格式化（38 个既有未格式化文件另开 `style:` PR）。
- 每个 wave 一个 worktree（`.worktrees/r2-w<N>`），合并前必须跑**集成验证**（多个 lane 一起 build+test+探针），再合并回 main。

## Review Focus

以下 5 类输入/条件，spec 没有逐字规定但最可能在使用中咬人，每个都必须在对应任务的测试里被钉住：

1. **键含换行/等号的字典键**（`Dictionary<string,int>` 且键含 `\n`、`=`）—— INI 的行式结构**不可承载**；用户会看到"数据静默截断"而不是报错。由 Task 6 的脚注 + Task 2 的守卫覆盖。
2. **抽象类型/基类链上的递归**（`abstract class Base { Base? Next }` 经派生类型生成）—— R1 因此红过 4 个 error（CS0144）。由 Task 1 强制。
3. **`List<HashSet<int>>` 这类"扩展集合作元素"** —— 今天在 4 个格式里**静默丢成员**且表说 ✅。由 Task 2 覆盖，并要求「表说 Supported ⇒ 生成代码必须含该成员」。
4. **YAML 的 null 与空值三态**（`null` / 缺失 / 空集合）—— 读侧会读、写侧无 API，用户会拿到"写进去是 `null`，读回来是缺失"。由 Task 4 强制。
5. **TOML 空表 + 后续表头**（`[V]` 后跟 `[W]`）—— 今天**永不返回**（D-9）。由 Task 0 强制，且它必须先于 Task 2/3 落地。

---

## Task 0: 修 TOML 空表挂死（D-9）— **必须最先做**

**为什么是第一个**：Task 2/3 会让 TOML 写出更多 `[V]` 空表，修之前每修一组就多一批挂死。

**Files:**
- Modify: `PicoToml/src/PicoToml.Gen/TomlSerializerGenerator.cs`（`ObjectStart` 分支，约 `:913-935`）
- Modify: `PicoToml/src/PicoToml/TomlReader.cs`（EOF 时的 token 语义，约 `:777-785`）
- Test: `PicoToml/tests/PicoToml.Tests/`（新增）

**Interfaces:**
- Consumes: 无（独立缺陷）
- Produces: 无新 API；`TomlReader` 在 EOF 时**必须**把 token 换成 `TokenType.None`（不得保持旧 token）。
  > **W0 实测修正**：`TokenType` 枚举里**没有** `EndOfFile`（`None=0, ObjectStart, ObjectEnd, …`），
  > 新增它会改**公共枚举**、违反 D5。`None` 是唯一可行解，且全链只有三处赋 `None`（初始化两处 + `FailRead` 且仅 `_isFinalBlock`），不会在合法输入上提前停。

**判据（来自 W2 的实测矩阵，必须逐条覆盖）**

| 输入 | 今天 | R2 目标 |
|---|:---:|:---:|
| `[V]\n` | HANG | 正常返回空字典 |
| `[V]` | HANG | 同上 |
| `[V]\n\n` | HANG | 同上 |
| `[V]\r\n` | HANG | 同上 |
| `[V]\n# comment\n` | HANG | 同上 |
| `[V]\na = 7\n[W]\n` | HANG | 正常返回，`W` 为空表 |
| `[V]\na = 7\n` | OK | **必须仍 OK**（回归锚点） |
| `""`（空文档） | OK | 必须仍 OK |

- [ ] **Step 1: 写 8 条往返测试**（上表逐条一条），断言 `Deserialize<M>(bytes)` **在 2 秒内返回**且值符合预期；用 `Assert.That(task).Completes()` 或显式 `CancellationToken` 超时，**不能**让测试自己挂住。
- [ ] **Step 2: 跑，确认 6 条挂起/超时**（用 `dotnet test --timeout` 或 TUnit 的 per-test timeout）。
- [ ] **Step 3: 修 reader**：`TomlReader.ReadSpan()` 在 EOF（final block）分支把 `_tokenType` 清为 `TokenType.None`（而不是只 `return false`）。
- [ ] **Step 4: 修生成器**：`ObjectStart` 分支在 `continue` 前补 `if (r.TokenType == TokenType.None) break;`。
  > **W0 实测修正**：原计划的 `if (!r.Read()) break;` **会吞掉下一个表头、静默丢数据**（由 `TwoPopulatedTables_BothKeepTheirKeys` 钉住）。
  > 且**表名不匹配**时该分支一次 `Read()` 都不发 ⇒ token 永远是 `ObjectStart`，守卫必须查 `None` 而**不能**依赖 `Read()` 的返回值。
  > 另有 3 处同族循环（`:1215` streaming、`:3381` poly、两处 `[[array]]` 内层）需一并守卫或加测试。
- [ ] **Step 5: 跑，全 8 条通过**；再跑全量 → 0 failed。
- [ ] **Step 6: 探针复核**：`toml|S9` 从 `TIMEOUT` 变 `OK 或 MISMATCH`（不再是 TIMEOUT）；`baseline-cells.txt` 相应更新。
- [ ] **Step 7: 提交**

## Task 1: 三项前置义务（基类链 root / 抽象类型 / 3 个模型）

**为什么独立**：Task 5（递归 4 格）依赖它；R1 的 W3 在这里红过 4 个 error。

**Files:**
- Modify: `shared/Gen/GenInfrastructure.cs`（`ExtractNestedPropertiesGuarded` 的 `out bool` → 携带 `RecursionKind`；`root` 参数透传）
- Modify: `tests/PicoSerDe.Integration.Tests/Models/`（`RvPolyBase` / `RvPolyLeaf` / `PolyRecAbstract` 视需要调整）
- Modify: 5 个生成器里实例化 `new T(...)` 的位置（抽象类型不可实例化时的降级路径）

**Interfaces:**
- Consumes: `FormatCapabilities.Decide`（R1）
- Produces: `internal enum RecursionKind { None, Self, Mutual }`；`ExtractNestedPropertiesGuarded(INamedTypeSymbol type, INamedTypeSymbol root, …, out RecursionKind recursion, …)`；站点写 `IsRecursiveRef: recursion != RecursionKind.None`

- [ ] **Step 1: 为三种环写测试**：①直接自递归（`Node { Node? Child }`）②2 节点互递归（`A → B → A`）③基类链（`abstract Base { Base? Next }`，经 `class Leaf : Base` 生成），各自断言「判定为 Self / Mutual / Self（不是 Mutual）」。
- [ ] **Step 2: 跑，确认基类链那条**把 `Leaf.Next` 判成 `Mutual`**（红）。
- [ ] **Step 3: 引入 `root` 参数**（最外层类型，6 个调用点全部传对，含 `GI:1706`/`GI:1788` 的透传）。
- [ ] **Step 4: 抽象类型的实例化路径**：生成代码在 `RecursiveMutual × toml = Supported` 生效后**不得** `new` 抽象类型；测试用一个抽象 `Base` + 派生 `Leaf` 覆盖。
- [ ] **Step 5: 跑三种环的测试 + 全量**，要求 0 failed（3 个受影响模型必须仍编译）。
- [ ] **Step 6: 提交**

## Task 2: 扩展集合 kind 家族（8 格：S7×4 + S8×4）

**根因**：`TypeKindResolver.cs:87` 一道门 + `"hashset"` 只在 json 生成器里出现 4 处，其余四个生成器**代码从未存在**。

**Files:**
- Modify: `shared/Gen/TypeKindResolver.cs:87` 附近（扩展集合的 kind 判定）
- Modify: `PicoJetson/src/PicoJetson.Gen/JsonSerializerGenerator.cs`（已有 4 处，作为模板）
- Modify: 其余 4 个生成器（MsgPack / Toml / Yaml / Ini）—— 按 json 的同形补
- Modify: `shared/Gen/GenInfrastructure.cs:1938`（`ExtendedKindAsElement` 的**无条件字面量判定**必须改成查表）
- Test: `tests/PicoSerDe.Integration.Tests/`（新增，跨 5 格式）

**Interfaces:**
- Consumes: `FormatCapabilities.Decide(fmt, Decision.ExtendedKindResolves)` / `Decide(fmt, Decision.ExtendedKindAsElement)`
- Produces: 无新 public API

- [ ] **Step 1: 写 8 条格的往返测试**（S7 成员 = `HashSet<int>`；S8 元素 = `HashSet<int>`，外层 `List<HashSet<int>>`），每格式一条，断言往返等价。
- [ ] **Step 2: 跑，确认 8 条红**（今天是静默丢成员）。
- [ ] **Step 3: 把 `:1938` 的字面量判定改成 `Decide(...) != Effect.Supported` 查表**，再逐个生成器补 kind 分支。
- [ ] **Step 4: 跑 8 条 → 全绿**。
- [ ] **Step 5: 翻表**：`Rules` 里 S7/S8 相关的 `ExtendedKind*` 格改成 `TargetRules` 的值。
- [ ] **Step 6: 加守卫测试**「`Decide(fmt, d) == Supported` ⇒ 探针生成代码里该成员必须出现」——**这条是防假绿的核心**，它必须能在"只翻表不改代码"时红（做一次实验证明）。
- [ ] **Step 7: 探针复核**：8 格从 MISMATCH → OK；`baseline-cells.txt` 用 `-t:Rebuild` 重生成。
- [ ] **Step 8: 提交**

## Task 3: 字典值为集合（4 格：S10×json/msgpack/toml/yaml）

**根因**：`GenInfrastructure.cs:2064` 的 `DictValueCollection` **无条件字面量判定，完全不问表**。

**Files:**
- Modify: `shared/Gen/GenInfrastructure.cs:2064`
- Test: `tests/PicoSerDe.Integration.Tests/`（4 条，json/msgpack/toml/yaml；ini 保持 ❌）

- [ ] **Step 1: 写 4 条往返测试**（`Dictionary<string, List<int>>`），断言往返等价；**ini 那条断言被丢 + 有诊断**（不是静默）。
- [ ] **Step 2: 跑，确认 4 条红**。
- [ ] **Step 3: 改成查表**（`Decide(fmt, Decision.DictValueCollection)`），再补各生成器的发射分支。
- [ ] **Step 4: 跑 → 绿；翻表；探针复核 4 格 OK**。
- [ ] **Step 5: 提交**

## Task 4: YAML 的 null 字面量（4 格：S5/S6/S12/S13）

**根因**：`YamlWriter` **没有 null 字面量 API**，而读侧已经会读（读写不对称）。

**Files:**
- Modify: `PicoYaml/src/PicoYaml/YamlWriter.cs`（新增 null 发射能力）
- Modify: `PicoYaml/src/PicoYaml.Gen/YamlSerializerGenerator.cs`（调用它）
- Test: `PicoYaml/tests/PicoYaml.Tests/` + `tests/PicoSerDe.Integration.Tests/`

- [ ] **Step 1: 写读写对称测试**：`null` / 缺失 / 空集合三态各自往返，断言三者**互不等价**（这是 R1′ 的核心，不能只断言"能读"）。
- [ ] **Step 2: 跑，确认写侧红**（读侧已绿，证明不对称）。
- [ ] **Step 3: 给 `YamlWriter` 加 null 字面量能力**（`~` 或 `null`，按 YAML 1.1/1.2 选一种并写进注释）。
- [ ] **Step 4: 生成器改用新 API；跑 → 绿；探针复核 4 格 OK**。
- [ ] **Step 5: 提交**

## Task 5: 递归 4 格（S11a/S11b × toml/yaml）— **依赖 Task 1**

**这是 R1 唯一因"会改变 wire"而推迟的一组。**

**Files:**
- Modify: `shared/Gen/GenInfrastructure.cs`（两处站点改用 `RecursionKind`）
- Modify: `DecisionTables.cs`（`Rules` 的 `RecursiveSelf`/`RecursiveMutual` 行翻成 `TargetRules` 的值）
- Test: `tests/PicoSerDe.Integration.Tests/`

- [ ] **Step 1: 写 4 条往返测试**（自递归 + 互递归 × toml/yaml），断言树同构往返。
- [ ] **Step 2: 跑，确认 4 条红**（今天一律丢）。
- [ ] **Step 3: 站点改用 `recursion`（Task 1 产出）而非 `bool`；翻表**。
- [ ] **Step 4: 跑 → 绿；探针复核 4 格 OK**；**README 矩阵（`MatrixCells`）随之变为终态口径**（Task 7 会把它接上 §6）。
- [ ] **Step 5: 提交**

## Task 6: 单品格（yaml S9 键转义 / toml S4）

**Files:**
- Modify: `PicoYaml/src/PicoYaml/YamlWriter.cs`（键转义）
- Modify: `PicoToml/src/PicoToml.Gen/TomlSerializerGenerator.cs`（S4 = `List<List<Dto>>` 的发射）
- Modify: `docs/superpowers/specs/…-design.md`（**ini S9 的脚注**，见下）

- [ ] **Step 1: 写 yaml S9 测试**（键含 `\n`、`"`、`:`），断言往返等价；写 toml S4 测试（`List<List<Dto>>`）。
- [ ] **Step 2: 跑，确认两条红**。
- [ ] **Step 3: 修 writer 的键转义 / toml 的嵌套表发射；跑 → 绿；探针复核 2 格 OK**。
- [ ] **Step 4: spec 修正（桶 C）**：`ini S9` 的 ✅ 加脚注「键字符集受限：不含 `\n`/`=`」；把该 boundary 值移入 R5 的清单，**不在 R2 实现**。
- [ ] **Step 5: 提交**

## Task 7: 让矩阵不再说假话（`MatrixCells` → §6 终态 + 表/代码一致性守卫）

**背景**：今天 `Rules[(toml, Mutual)] = Supported` 但守卫照丢 ⇒ README 矩阵在说假话。Task 2 的守卫只覆盖"成员是否出现"，这一任务补上"矩阵口径"。

**Files:**
- Modify: `shared/Gen/FormatCapabilities.cs`（`MatrixCells` 从「现状」改为「终态」口径）
- Modify: `tests/PicoSerDe.Integration.Tests/FormatCapabilitiesTests.cs`（README 校验改为对 §6 终态）
- Modify: `README.md`（矩阵按新口径重新生成 5 行）

- [ ] **Step 1: 写测试**：`MatrixCells(shapeId)` 的 5 个 token 必须等于 §6 终态（14 形状里 README 覆盖的 5 个）。
- [ ] **Step 2: 跑，确认红**（今天 `MatrixCells` 用的是 `Rules`）。
- [ ] **Step 3: 改成 `DecideShapeTarget` 口径；README 重新生成**。
- [ ] **Step 4: 跑 → 绿**；并做一次负控制（把 §6 某一格改错 → 测试红）。
- [ ] **Step 5: 提交**

## Task 8: 既有缺陷收尾（D-10 / D-8 / I-6 白区）

- **D-10**：`PICOSERDE005`（匿名类型）补 ⏸ 标记 + `reopen:` + 真实 `Location`；把该用例加进「每条 drop 消息都带标记」的测试（样本数 3 → 4）。
- **D-8**：匿名类型双口径并入共用管线（第 6 个发射面）。
- **I-6 白区 8 条**：`±Inf`、null-collection、`DateTimeKind.Unspecified`、S15 的字符串/分隔符覆盖等 —— 逐条补边界值往返测试。

- [ ] **Step 1: D-10 的测试先红**（断言匿名类型 drop 也带标记）。
- [ ] **Step 2: 实现 → 绿**。
- [ ] **Step 3: D-8 并入管线**（按 spec §5.6 的归属）。
- [ ] **Step 4: 逐条补 I-6 白区测试**，每条先红后绿。
- [ ] **Step 5: 提交**

## Task 9: 发布准备（`x+1`）

- [ ] **Step 1: 全量门禁**：build 0/0、全量 0 failed、探针 70 格中**终态 ✅ 的格全部 OK**、`api/` 零 diff。
- [ ] **Step 2: AOT**：`dotnet publish PicoJetson/samples/PicoJetson.Sample/… -p:PublishAot=true` → `exit=0`。
- [ ] **Step 3: 写 `docs/r2/gate-evidence.md`**（真实输出，逐条命令可复现；沿用 R1 的教训：**不要**把未跑的命令写进去）。
- [ ] **Step 4: 发布说明**（`x+1`）：列出**哪些格从 ❌ 变 ✅ / 哪些 wire 输出变化**，**不写测试数量**。

---

## Wave 编排

| Wave | 任务 | 并发 | 理由 |
|---|---|---|---|
| **W0** | Task 0（TOML 挂死） | 单 | **必须最先**：否则后续每组都会多一批挂死 |
| **W1** | Task 1（三项前置义务） | 单 | Task 5 依赖它；且它单独就会红/绿交替 |
| **W2** | Task 2（8 格）+ Task 3（4 格） | **2 条并行** | 文件不相交（`TypeKindResolver` vs `GenInfrastructure:2064`） |
| **W3** | Task 4（YAML null）+ Task 6（单品格） | **2 条并行** | `YamlWriter` 与 toml/yaml 生成器；Task 6 的 yaml 键转义与 Task 4 同文件但对不同函数，需**同一 worktree 顺序做**（见下注） |
| **W4** | Task 5（递归 4 格） | 单 | 依赖 W1 |
| **W5** | Task 7（矩阵口径） | 单 | 依赖 W2/W4 的表已翻完 |
| **W6** | Task 8（D-10/D-8/I-6） | 单 | 与主线无依赖，可提前到任意 wave 之后 |
| **W7** | Task 9（发布准备） | 单 | 汇总 |

> **W3 的注**：Task 4 与 Task 6 都会改 `YamlWriter.cs`（不同函数）—— 为免冲突，**放在同一 worktree 顺序执行**，不并行成两条分支。

## 每个 wave 的出口门禁

| Wave | 出口条件 |
|---|---|
| W0 | 8 条 TOML 测试全绿；`toml\|S9` 不再是 TIMEOUT；全量 0 failed |
| W1 | 3 种环的判定正确（含基类链）；3 个受影响模型仍编译；全量 0 failed |
| W2 | 12 格从 MISMATCH → OK；表/代码一致性守卫能红（实验证明）；全量 0 failed |
| W3 | 6 格 OK；`YamlWriter` 读写对称；ini S9 的脚注已写进 spec |
| W4 | 4 格 OK；README 矩阵口径已变；全量 0 failed |
| W5 | `MatrixCells` = §6 终态；README 重建；负控制红 |
| W6 | D-10/D-8/I-6 全部有测试；8 条白区清零 |
| W7 | build 0/0、全量 0 failed、AOT exit=0、`api/` 零 diff、门禁证据入库 |

## 自检（写完后执行）

1. **spec 覆盖**：§6 的 24 格是否每格都有任务认领？（Task 2: 8、Task 3: 4、Task 4: 4、Task 5: 4、Task 6: 2、Task 0: 1（toml S9 的**挂死**部分）、Task 6 的 spec 修正: 1（ini S9））
   > **W0 实测修正**：`toml|S9` 由 Task 0 只解决**挂死**；其 MISMATCH 的其余原因（写侧裸 key 不转义，
   > `TomlWriter.NeedsKeyQuoting` 不含 `
`/``/控制字符）已改挂 **Task 6**。即 Task 0 认领的是「不再 TIMEOUT」，不是「变 OK」。
2. **没有"只翻表"的任务**：每个翻表的 Step 都紧跟一个探针实测 Step。
3. **类型一致**：`RecursionKind`、`MatrixCells`、`Decide/DecideShapeTarget` 的签名与前序任务一致。
4. **比例**：本计划约为 spec 的 1/2 —— 若某任务需要写大段代码，说明该任务的边界划错了。

