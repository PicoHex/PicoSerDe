# 融合读取契约（fused token read）实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 `docs/superpowers/specs/2026-10-28-fused-token-reader-design.md`（下称 **spec**）的六个契约成员落成**派生接口 `IFusedTokenReader` + 静态参考正本 `TokenReaderFused`**（原 DIM 路线已被实测否决，spec **§R1**），并让五个 reader 在**证明等价**之后各自融合一次分派 —— 不是把 `Read()` 复制一遍（spec §10、spec §1.2 引 4e 报告 `:271-274`）。

**Architecture:** 参考正本（`TokenReaderFused` 的 `ref T` 静态方法）为规范，reader 的接口实现为对象；实现只许在**成功分支**分叉、`default:` 一律退回正本，任何可观测差异（token 类型、span 内容、全部 getter、`Depth`、`BytesConsumed`、`NeedsMoreData`、异常类型与消息）都不许变（spec §3.2.3）。等价性由**同一构建内**的「参考路径 vs 融合路径」全量 token 转储逐字节对拍证明（spec §5.1/§5.2），不由「看起来很融合」证明。

**Spec:** `docs/superpowers/specs/2026-10-28-fused-token-reader-design.md`（**v2 / R1 修订后 610 行**；初版 451 行已合并于 `22f08c6`，机制修订见 spec §R1）。
**格式范本:** `docs/superpowers/plans/2026-10-27-perf-json-msgpack.md`。

**计划作者只写文档、不写代码。** 本文件不含任何实现代码：只有任务边界、验证命令、验收断言与回退路径。

---

## 0. Global Constraints（每个任务都必须过）

- **构建**：`dotnet build PicoSerDe.slnx -c Debug -m:1` → **0 warning / 0 error**。
- **测试**：`dotnet test PicoSerDe.slnx -c Debug --no-progress` → **0 failed**（不引用测试条数：本计划刻意不写测试数量）。
- **`api/*.public.txt` 零 diff（每个任务，无例外）**：提交后 `git status --short api/*.public.txt` 必须为空。理由：这些基线记录的是「上次发布的面」，手改等于伪造发版 diff（spec §4.1 A-4，`api/PENDING-CHANGES.md:3-7`）。
- **`api/PENDING-CHANGES.md` 只许三个任务碰**：`T2`（R-3 新增行，spec §4.2）、`T13`/`T17`（F-1/F-2 的行为修正段落，spec §4.3，走 `api/PENDING-CHANGES.md:18-30` 的既有体裁）。其余任务对它也必须零 diff。
- **格式**：提交前 `dotnet csharpier format`（仓库本地工具，`dotnet-tools.json`）。
- **单任务预算**：人手编写的改动 **≤ 200 行**。「`tests/FormatCapabilities.Probe/generated-baseline/` 重生成」按 spec §3.4.2 C-4 属机械产物，**单列、不计入该预算**——否则任何发射改动都必然超过 200 行，预算就失去意义。评审须确认 golden diff 里只有预期形状的变化。
- **新 worktree 的 restore 前置**：新建 worktree 后先 `mkdir -p artifacts/nupkg`，否则 `dotnet build` 直接 `error NU1301: The local source '.../artifacts/nupkg/' doesn't exist`（本次实测，`NuGet.config` 的 `local` 源指向该 gitignore 目录）。
- **`.superpowers/` 是 gitignore 的**：`.gitignore:415` 就是 `.superpowers/`，`git ls-files .superpowers` = **0 个文件**（实测）。因此：① 执行报告 `.superpowers/sdd/plan-fused/report.md` 与所有「原始输出尾部」只存在于本地，是**证据**不是交付物；② **可复现的测点工具不许放这里** —— T4 的微基准放**已跟踪**的 `PicoMsgPack/tools/pertoken/`（照 `PicoMsgPack/tools/readspan/` 的既有工具约定）；③ 4e 的 `DumpTokens` harness 落在 `.superpowers/sdd/perf-mp-readspan/harness/` 下、**并未入库**，只能当本地留证，**不构成任何任务的前置依赖**。
- **一个任务一个 commit**：中途停止不留半成品。任何任务被 revert 都不需要「顺手修别处」。
- **不 push / merge / 碰主工作树**（与 spec RK-5 缓解②一致）。

---

## 1. 收益纪律（RK-6）—— 先测后改 / 改后立即测

spec §9 U1 明确：融合后能回收「每 token 成本」的 **30–50%** 是**来自 MP 报告 §5.1 M2 的估算，幅度未证实**；spec §9 U2 明确：13.4 ns/token（我们）/ 6.8 ns/token（竞品）是**算术推得、非实测**。因此本计划不把任何百分比当已知事实，而是把 U1 的**下限 30%** 当作**预注册预期（pre-registered expectation）**，并据此写出停止规则：

> **停止规则（对每一个带性能诉求的任务 `T6/T7/T8/T11/T14/T15/T18` 生效；`T9` 单独**不**触发）**
> 1. 任务开工前必须先跑一次基线（`T4` 的微基准 + 相关 bench 行），记录到 `.superpowers/sdd/plan-fused/report.md`。
> 2. 提交后**立即**在同一台机器、同一进程协议下重测（README `#performance` 的协议：一轮 = 一个进程，每臂 5 次独立运行 × ≥100k 迭代取中位，每图取**三轮中位**，`README.md:61-76`）。
> 3. **若实测收益 < 预注册预期的一半（即 per-token 成本回收 < 15%）⇒ 停下、写报告、不再往上堆**。允许在任意任务处把后续步骤整体转为欠账（spec RK-6：允许在 S1 处终止后续步骤，把契约 S0 作为独立成果保留）。
> 4. 报告里**只许写实测值**，并标出「三轮中位 / 单轮 / 噪声内」三种判定之一；禁止把估算写成结论（spec §2.2 N6：本规格不承诺任何性能数字）。

**为什么 `T9` 单独不触发停止规则**：spec §3.4.1 已论证 E-1（计数循环）与现状发出的 `Read()` 次数**完全一致**，它单独只能省「每元素一次 `_tokenType` 比较」，真正的收益在 **E-3**（spec §3.4.1 末句、spec §9 **U5** 把「E-1 单独能省多少」列为**未测**）。所以 `T9` 只要求**可分辨且不倒退**；**T9+T11 累计**才进停止规则的判定（这也是 spec §9 U5 要求「拆成两次独立提交，各测一次」的原因：测得到，但不拿它单独卡停）。

4e 的先例就是这条规则的理由：计划预期 `DES MTenInts` 2.33× → 1.5×，实测只拿到 **2.03×**（spec RK-6 引 4e 报告 §5.6）。

---

## 2. 并行冲突处置（RK-5）—— 开工闸门 **G-0**

> **这一节是硬闸门。T1–T11（以及后续任何触及 `PicoMsgPack` 的任务）在 G-0 通过之前一个字都不许写。**

### 2.1 事实（本文写作时实测，不是推测）

- `git worktree list` 显示并行工作树 `.worktrees/mp-collection`，branch `perf/mp-collection`，HEAD = `40e5683`（`main` 的祖先）。
- 该工作树**有未提交改动**（`git status --short`）：
  - `M PicoMsgPack/src/PicoMsgPack/GlobalUsings.cs`
  - `M PicoMsgPack/src/PicoMsgPack/MsgPackReader.cs` —— `git diff --stat`：**2 files changed, 98 insertions(+), 115 deletions(-)**，`MsgPackReader.cs` 本身 212 行变动。
  - `?? PicoMsgPack/tests/PicoMsgPack.Tests/MsgPackReaderIntegerCacheTests.cs`
- 该改动的方向是 **在 `Read()` 里顺手算出整数值并缓存**（新字段 `_intValue` / `_intKind`，注释自称 `#5 (task perf/mp-collection)`），目的是让 `TryGetInt32`/`TryGetInt64` **不再二次派生**（`TokenType` × `_valueSpan.Length` 双重 switch）。
- **这就是同一个目标的第二条路径**：spec 的答案是「把分类与取值合成一个成员」，mp-collection 的答案是「在分类时把值存下来」。两者**落在同一批 `Read()` 的 `switch` 分支上**（对拍点：`MsgPackReader.cs:404-415` 的 fixint/fixmap/fixarray、`:463-596` 的 `0xCC`–`0xDF` 家族）。
- 截至本文写作时，该工作树**尚未**触及 `PicoMsgPack.Gen`（`git diff --stat` 只有两个文件）——但 spec RK-5 已预判它的方向（`TryReadInt32Array` 接入实验）会落到 §3.4.2 C-2 的发射点上。

### 2.2 闸门 G-0（必须逐条留证）

| 条件 | 判定 | 留证方式 |
|---|---|---|
| **G-0a** | `.worktrees/mp-collection` **已合并进 `main`** 或 **已明确停止**（工作树干净 `git status --short` 为空 + 分支声明废弃） | 把 `git -C .../mp-collection log --oneline -1` 与 `git status --short` 的输出粘进 `.superpowers/sdd/plan-fused/report.md` |
| **G-0b** | 若已合并：**先读它的结论**，再判定 T5–T11 是否还成立 | 写清三点：① 它是否已经消掉了 int 的二次分派；② 若是，T6/T7 的预注册预期必须按实测重新登记（不许沿用 U1 的 30%）；③ 若两条路径重叠，**以实测为准、以 spec 的等价性门槛为约束**（spec RK-5 缓解②） |
| **G-0c** | 若已合并且结论是「整数二次分派已消失」：**跳过 T6/T7**，直接从 T8（`TryReadFloat64`/`TryReadBool`）与 T5（count）继续 | 在 report 里写明跳过理由与对应 spec 条目 |
| **G-0d** | 排序纪律：T5–T11 的发射点改动与 mp-collection 的 `Collection` 快路径**高度重叠**，必须在**同一时间窗口由同一人排序**（spec RK-5 缓解③） | 在 report 记录窗口起止与执行人 |
| **G-0t**（轻闸门） | **只改 `PicoMsgPack/tests/` 的任务（T3）只走这一条**：`git -C .worktrees/mp-collection status --short PicoMsgPack/tests/` 为空。本文写作时实测为空 | 命令输出贴进 report |

**为什么闸门放在最前面而不是「边做边看」**：两者改的是同一个 `switch` 的同一批分支，合并冲突是文本级的（无法靠 rebase 自动消解），而结论冲突是语义级的（同一次收益被两条路径各记一次）。spec RK-5 只允许一条处置路径：先看结论，再排序。

### 2.3 不受 G-0 阻塞的任务

`T1`（Core 契约）、`T1b`（五个 reader 的转发实现）、`T2`（`api/` 登记）、`T3`/`T4`（护栏与测点）、`T12`–`T18`（JSON / TOML）**不碰 `PicoMsgPack` 读侧的优化路径**，可以先行（`T1b` 会碰到 `MsgPackReader.cs`，但只加 6 个转发成员、不碰 `Read()`/`TryGet*` 的 `switch`，与 RK-5 的重叠面为零；若 mp-collection 同时在改同一文件的成员区，按 G-0d 同一时间窗口排序）。其中 `T3` 只改 `PicoMsgPack/tests/`，过 **G-0t** 即可（本文写作时实测为空）；`T4` 只读现有 reader、不改代码，不需闸门。`T12`–`T18` 完全不碰 `PicoMsgPack`。

---

## 3. 等价性基线怎么建（spec §5）

### 3.1 决定：MsgPack 直接复用；其余四格式「按需先建」，不做一次性建齐

| 格式 | 语料现状（实测） | 决定 | 成本 |
|---|---|---|---|
| **MsgPack** | **已有**：`PicoMsgPack/tests/PicoMsgPack.Tests/MsgPackSpanTokenDump.cs`（663 行）。`Payloads()` `:44`（53 个命名 payload）、`MalformedPayloads()` `:160`（8 个畸形）、`RandomPayloads()` `:183`（2000 个确定性随机，种子 `20261027` 在 `:185`）、`SmallPayloads()` `:356`（≤512 字节）。4e 用它产出过 **804 849 行**转储（spec §5.2） | **直接复用，0 成本** | 0（T3 只加一条新走法） |
| **JSON** | **无**。全仓 `find -iname "*TokenDump*"` 只命中 MsgPack 那一个文件（实测） | **必须先建**（spec §5.2：「没有对拍就不许改」是本规格唯一的准入条件） | T12，≤190 行 |
| **TOML** | **无** | **条件建**（只在 T16 被触发时才建；spec §6 S3 允许停在 S2 并把 TOML 记为欠账） | T16，≤190 行 |
| **YAML / INI** | **无，且不需要** | **不建**。两者没有可计数的集合形态（spec §3.3：YAML 全文无 `TokenType.ArrayStart` 赋值，INI 全平），spec §6 S4 明确「不做任何 reader 改动」⇒ 没有覆写就没有对拍需求 | 0 |

**明确否决「四格式一次性建齐」**：spec RK-4 的原文就是「S2/S3 前置成本被低估」。一次建齐会把 4 份语料成本压在前面，而 YAML/INI 的那两份按 spec §6 S4 **永远不会被用到**。所以策略是**按需前置**：谁要覆写，谁先有语料；不覆写的格式不建。

### 3.2 成本估计的置信度（必须写在计划里）

- **高置信**：MsgPack 复用 = 0 行（语料已存在且已被 4e 验证过）。
- **中置信**：JSON 语料 ≈ 1 个任务（T12，≤190 行）。**若实测超 200 行，就按 T12a（payload 矩阵）/ T12b（双走 walker）切开**，不许把一个 400 行的任务塞进一次 commit。
- **低置信**：TOML 语料（T16）能否控制在 190 行内。spec §9 U8 明确「24 处发射点里有多少最终能被共享发射器覆盖」**未逐点判定**，同理「TOML 需要多少 payload 才能钉住它的 token 家族」也未判定。T16 若超预算，按 T16a/T16b 切开，或直接按 spec §6 S3 判为欠账。

### 3.3 对拍的构造（这是 T3 / T12 的实现约束，不是建议）

spec §5.2 的增量要求是 **「参考 vs 融合」两条路径在同一份二进制里共存**，不是 4e 的「改前 / 改后」跨日志对比。落法：

1. **参考侧**：`Read()` 驱动 + `TryGetX()` —— 仓库里就是 `MsgPackSpanTokenDump.DrainSpan()` `:360` / `DrainSequence()` `:379` / `DrainStreaming()` `:401` 的形状。
2. **融合侧**：同一个 payload、同一入口，元素体换成 `TryReadInt32` / `TryReadCollectionCount` 驱动的计数循环。
3. **断言**：两侧的 `MsgPackTokenSnapshot` 列表**逐项相等**，且行数相等。
4. **快照字段是否够用**：`Capture()` `:434` 已覆盖 spec §5.1 要求的「全部 getter」——`GetStringRaw()`、`TryGetInt32`、`TryGetInt64`、`TryGetUInt64`、`TryGetFloat64`（按位 `X16` 打印）、`TryGetBool`、`TryGetExtension`（含 tag），加上 `TokenType`、`BytesConsumed`、`Depth`。**唯一缺口是 `NeedsMoreData`**，由 `DrainStreaming()` `:401` 的流式完成判定单列（它读 `reader.NeedsMoreData`）。异常类型与消息由 `MalformedPayloads()` 分支单记一行（同 4e 做法）。
5. **融合侧怎么选访问器**：按**参考侧记录的 `TokenType`** 选（int 家族 → `TryReadInt32`，float 家族 → `TryReadFloat64`，bool → `TryReadBool`，其余 → `Read()`）。这不是答案泄漏：生成代码在生成期就知道元素是哪个标量族（spec §3.4.3），值本身仍由融合路径**独立解出**并逐位比对。**禁止**用参考侧的值去校验融合侧的值（那就成了自证）。

---

## 3.5 TDD 前置规则（对 T3 起的每个任务生效）

**规则：先写会红的测试，看着它红，再写实现。没看着它红过的测试，不算数。**

- 每个任务的**第一步**是提交（或在 worktree 里先跑出）**失败的测试**，并把**红的结果**贴进报告。
  红的形式有两种，必须在报告里说清是哪一种：
  1. **编译红**：被测成员尚不存在（典型：给 reader 加接口实现）。
  2. **断言红**：能编译但断言失败（典型：值不对、`NeedsMoreData` 状态不对、token 流不同）。
- 然后写**最小实现**让它变绿；`git diff` 里**不允许**出现"先写实现再补测试"的痕迹。
- **性能类覆写（T6–T8、T14）的特殊说明**（不是豁免）：这类改动的**行为**按设计不变，所以"覆写前后行为相同"的测试在覆写前就是绿的 —— 它给不出红。
  因此它的红必须来自**护栏本身**：故意让被覆写的成员返回错值，确认护栏**变红**；把红的结果贴出来，再恢复。
  这就是 §5.2「双走 dump」护栏存在的理由，也是 T3 必须先于 T5–T8 的原因。
- 违反本规则的任务**不合并**，即使全绿。

> 本条为 process amendment：T1/T2 是在实现之后补测试的（T1 的契约与参考正本是先写代码后写 574 行测试）。
> 那两步已经合并，不回滚；但从 **T3 起**按本条执行，且后续任务不得沿用"先写实现"的做法。

## 3.6 T3 裁决（controller, 2026-10-29）

T3 交付 `ca07715`（转储设施）+ `0f95029`（护栏与负控），并提了 5 条偏差。裁决如下：

| 编号 | 内容 | 裁决 | 理由 |
|---|---|---|---|
| **D-1** | 编译红**不进提交**（进了会让构建失败，与"0 warning / 0 error"冲突），改为报告内贴原文 + **一条命令可复现** | **接受** | 红是为了"看着它红过"，不是为了把仓库留在红状态。复现命令必须留在报告里 |
| **D-2** | 契约臂打 `reader.TryReadX(...)`（接口），**不是**直接打 `TokenReaderFused` | **接受，且这是唯一正确做法** | 直接打正本会让 T5–T8 的覆写**对护栏不可见**（恒绿）⇒ 护栏失去意义 |
| **D-3** | 797 行 vs 计划 190 行；已按"超 200 行就切"拆成 T3a/T3b 两个提交 | **接受** | 超出部分是"护栏要当判据"的必需项（可注入故障的替身、`IntAccessor`、独立 count 列），计划漏估 |
| **D-4** | 新开 `MsgPackFusedTokenDump.cs`，不改 4e 的 `MsgPackSpanTokenDump.cs` | **接受** | 后者被 4e 的树外 golden harness 用 `<Compile Include>` 链接，`allows ref struct` 会改其编译前置 |
| **D-5** | `SEQ`/5 档分块入口归 T6 矩阵 | **接受** | T3 已覆盖 `SPAN` 真/假 × 三条语料 × 双整数访问器 |

**对 T5 / T6 追加的验收（本次裁决新增）**：
> 当 T5/T6 让 `MsgPackReader` 实现 `IFusedTokenReader` 后，T3 护栏的**真实 reader 臂必须真正跑起来**
> （即 `DrainFused<MsgPackReader, …>` 可编译且纳入语料）。仅仅"替身 reader 绿"**不算** T5/T6 完成。
> 这是防止护栏在替身上永久自证、而真实 reader 从未被覆盖。

## 4. 任务总表

任务是**串行**的（一个 commit 接一个），编号 `T*`；「spec 步」列指向 spec §6 的步骤编号，两套编号不混用。

| # | 一句话 | spec 步 | 依赖 | 预算（人手行） | 闸门 |
|---|---|---|---|---|---|
| T1 | 契约落地：`IFusedTokenReader`（6 抽象成员）+ `TokenReaderFused` 正本，**并先证机制可用** | §6 **S0**、§3.1/§3.2、**§R1** | — | ~190 | — |
| T1b | 五个 reader 实现 `IFusedTokenReader`（不优化处 = 一行转发）+ 一致性断言 | §6 **S0**、§3.1 末段 | T1 | ~120 | — |
| T2 | `api/PENDING-CHANGES.md` 登记 **R-3**（新接口 + 新静态类） | §4.2 | T1 | ~10 | — |
| T3 | MsgPack「参考 vs 融合」双走 dump 护栏（先证明护栏有效） | §6 **S0**、§5.2 | T1b | ~190 | G-0t |
| T4 | per-token 微基准闸门（10⁶ 次，settles §9 U2 方向） | §9 U2 | T1b | ~120 | — |
| T5 | MsgPack `HasCountedCollections` + `TryGetCollectionCount` | §6 **S1** | T3 | ~90 | G-0 |
| T6 | MsgPack `TryReadInt32` 覆写 | §6 **S1** | T5 | ~150 | G-0 |
| T7 | MsgPack `TryReadInt64` 覆写 | §6 **S1** | T6 | ~90 | G-0 |
| T8 | MsgPack `TryReadFloat64` / `TryReadBool` 覆写 | §6 **S1** | T6 | ~110 | G-0 |
| T9 | 生成器 **E-1** 计数循环（仅 MsgPack 标量元素站点） | §6 **S1**、§3.4.1 | T5 | ~150 | G-0 |
| T10 | 生成器护栏：产物不含 `ITokenReader` | §3.4.3 | T9 | ~50 | G-0 |
| T11 | 生成器 **E-3** 融合元素读（仅 MsgPack 标量元素站点） | §6 **S1**、§3.4.1 | T9、T6/T7/T8 | ~130 | G-0 |
| T12 | JSON「参考 vs 融合」双走 dump 护栏（新建语料） | §6 **S2** | T1b | ~190 | — |
| T13 | 删除 JSON `TryReadNextInt32Seq`（**F-1**）+ 负数回归 | §6 **S2**、§4.3 | T12 | ~130 | — |
| T14 | JSON `TryReadInt32` 覆写 | §6 **S2** | T13 | ~150 | — |
| T15 | JSON **E-3** 发射（条件任务） | §6 **S2** | T14 | ~120 | — |
| T16 | TOML 双走 dump 护栏（条件任务） | §6 **S3** | T12（复用走法） | ~190 | — |
| T17 | TOML **F-2** 重做（条件任务） | §6 **S3**、§4.3 | T16 | ~140 | — |
| T18 | TOML `TryReadInt32` 覆写 + E-3（条件任务） | §6 **S3** | T17 | ~140 | — |

**总计：19 个任务，人手改动约 2 460 行（不含 golden 重生成）。** 条件任务 T15–T18 可在 T14 之后按 §1 的停止规则整体转为欠账。

**为什么 T1 要拆出 T1b**：`ref struct` 拿不到默认体（CS9245，spec §R1.1）⇒ 六个成员是**五个 reader 各写六份**的实体代码，和「契约 + 正本」不是同一个量级。合在一起会破 §0 的 ≤200 行预算。

**spec §6 S4（YAML / INI）不是独立任务**：spec §6 S4 的验收原文是「不做任何 reader 改动；仅由 S0 的一致性测试断言 `HasCountedCollections == false` 与 `TryGetCollectionCount` 恒 `false`」—— 这条由 **T1b** 的验收第 2 条覆盖（五个 reader 全断言 `false`，T5 再把 MsgPack 那一行改成 `true`）。见 §6「不做的事」。

---

## 5. 任务详情

每条任务的字段固定：**改哪里 / 怎么验证 / 验收标准 / 依赖 / 回退 / 预算**。带性能诉求的任务另有 **预注册预期 + 测点**。

---

### T1 — 契约落地：`IFusedTokenReader` + `TokenReaderFused` 参考正本，**并先证机制可用**（spec §3.1/§3.2、§6 S0、§R1）

**目的**：在写任何 reader 代码之前，先用编译器把「这套机制能不能用」问出答案。上一版计划正是**跳过了这一步**，才在 T1 撞上 30 个 `CS9245`（spec §R1.1/R1.2）——机制没证就落地，是一次白跑的整轮任务。

**改哪里**

**第 1 步（可行性证明，必须最先做，产出进 report）** —— 在 `.superpowers/sdd/<本任务>/probe/` 下建**独立最小工程**（gitignored、**不入库**；每个子工程带自己的 `Directory.Build.props` 与 `NuGet.config`，与仓库构建设置隔离），逐条复现 spec §R1 的判据：

| # | 探针 | 必须得到 |
|---|---|---|
| P-1 | 6 个成员**带默认体**加进一个 `ITokenReader` 形状的接口 + `ref struct` 实现 | **`error CS9245`**（spec §R1.1） |
| P-2 | 同形状换成**抽象**成员 | **`error CS0535`**（spec §R1.1 第 4 条） |
| P-3 | 派生接口 `IFusedTokenReader` + `ref struct` 实现 6 成员（含 `ref this` 一行转发、`where T : IFusedTokenReader, allows ref struct` 见证） | **0 error**，且**位置真的推进**、见证派发到 reader 自己的实现（spec §R1.4 B-1…B-3） |
| P-4 | 扩展方法三种形状：按值 `this T`、`this ref T`、C# 14 `extension<T>(ref T reader)` | 按值：**编译通过但位置不推进**（spec §R1.3 A-1）；`this ref T`：**`CS8337`**；`extension<T>(ref T)`：**`CS9300`** |

**判据**：P-1…P-4 的实测结果与 spec §R1 记录**逐条一致**才允许进第 2 步。任何一条不一致 ⇒ **停下报告**（说明语言/工具链行为变了，机制要重新裁定，不许在本任务里自行改设计）。

**第 2 步（契约落地）**
- `PicoSerDe.Core/src/ITokenReader.cs`：在 `ITokenReader` 接口（`ITokenReader.cs:13-29`）**之后追加**两块，按 **spec §3.1 / §3.2 逐字**：
  - `public interface IFusedTokenReader : ITokenReader` —— 6 个成员**全部抽象**（`HasCountedCollections` 属性 + `TryGetCollectionCount` + 四个 `TryReadX`），XML 文档逐字搬运；
  - `public static class TokenReaderFused` —— 4 个 `public static bool TryReadX<T>(ref T reader, out T value) where T : ITokenReader, allows ref struct`，体 = spec §3.2.1 的伪代码（`Read()` → `TryGetX()` 字面组合）。
  - **`ITokenReader` 接口体一个字节不动**（spec §3.1 首句）：不改既有成员、不往里加任何成员。
- **不改** `tests/PicoSerDe.Integration.Tests/TokenReaderConformanceTests.cs`：一致性断言要等五个 reader 实现成员（T1b）。

**怎么验证**
```bash
# 接口与正本单独可编译，且 ITokenReader 未被动过
dotnet build PicoSerDe.Core/src/PicoSerDe.Core.csproj -c Debug -m:1     # 0 warning / 0 error
git diff -U0 -- PicoSerDe.Core/src/ITokenReader.cs                      # 只许 + 行
grep -c "ITokenReader" api/PicoSerDe.Core.public.txt                    # 与改动前同值（基线未动）

dotnet build PicoSerDe.slnx -c Debug -m:1                               # 0 warning / 0 error
dotnet test PicoSerDe.slnx -c Debug --no-progress                       # 0 failed
git status --short api/                                                 # 必须为空
```

**验收标准**（全部可判定）
1. **机制可行性已先证**：P-1…P-4 四条判据全部与 spec §R1 一致，原始输出尾部进 report；不一致 ⇒ 本任务 **BLOCKED**，不许把「应该可以」当结论。
2. 构建 **0 warning / 0 error** —— 关键判据：`ITokenReader` 未改 ⇒ 五个 reader 与所有外部形状的实现者**一行都不用改**（这正是 DIM 做不到的，spec §R1.2）。
3. `git status --short api/` 为空 ⇒ `api/*.public.txt` 零 diff（spec §4.1 A-4）；新增的公共面由 **T2** 登记。
4. `git diff -U0 -- PicoSerDe.Core/src/ITokenReader.cs` **只有 `+` 行**（`ITokenReader` 接口体零改动，spec §3.1）。
5. `TokenReaderFused` 的四个方法首参都是 `ref T reader`（`grep -c "ref T reader"` = **4**）—— 按值首参会推进副本（spec §R1.3 A-1 的实测）；对应的 reader 成员**不许标 `readonly`**（`CS1605`，spec §R1.6 `probe/08`）。
6. `IFusedTokenReader` 的六个成员都是**抽象**（无方法体）：`grep -c "bool .*{" PicoSerDe.Core/src/ITokenReader.cs` 里新接口体内命中为 **0**。

**依赖**：无（**无需** G-0，不碰任何格式的 reader）。
**回退**：`git revert <T1>`。纯追加块 + 零 reader 改动 ⇒ 单点回退是干净的；T1b 起依赖它，必须逆序回退。
**预算**：~190 行（约 100 行是 spec §3.1 的 XML 文档逐字搬运 + 4 个正本方法体 + 探针判据摘要）。

---

### T1b — 五个 reader 实现 `IFusedTokenReader`（不优化处 = 一行转发）+ 一致性断言（spec §6 S0、§3.1 末段）

**为什么单独一个任务**：`ref struct` 拿不到默认体（spec §R1.1）⇒ 六个成员是**五个 reader 各写六份**的实体代码。与 T1 的「契约 + 正本」不是同一个量级，合起来会破 §0 的 ≤200 行预算；分开后两者也各自可独立回退。

**改哪里**
- 五个 reader 的声明从 `: ITokenReader` 改成 `: IFusedTokenReader`（派生接口 ⇒ 旧成员照旧实现，无需改动任何既有成员），并各实现 6 个成员：
  `JsonReader`（`PicoJetson/src/PicoJetson/JsonReader.cs:27`）、`MsgPackReader`（`PicoMsgPack/src/PicoMsgPack/MsgPackReader.cs:14`）、`TomlReader`（`PicoToml/src/PicoToml/TomlReader.cs:20`）、`YamlReader`（`PicoYaml/src/PicoYaml/YamlReader.cs:19`）、`IniReader`（`PicoIni/src/PicoIni/IniReader.cs:15`）。
  - **本任务里 6 个成员全部是转发**（零优化，优化留给 T5–T8/T14/T18）：
    ```csharp
    // 纯查询：readonly 可以（也不该改状态）
    public readonly bool HasCountedCollections => false;
    public readonly bool TryGetCollectionCount(out int count) { count = 0; return false; }

    // 融合读：会推进 reader ⇒ 不能标 readonly
    public bool TryReadInt32(out int value) => TokenReaderFused.TryReadInt32(ref this, out value);
    // TryReadInt64 / TryReadFloat64 / TryReadBool 同形
    ```
    **为什么不标 `readonly`**：`readonly` 成员里 `this` 是只读变量，`ref this` 编译失败 **`error CS1605`**（实测，spec §R1.6 的 `probe/08`）。
  - 成员必须**成员可见性为 public**（不能写成显式接口实现）：生成器在**具体类型**上调用（spec §3.4.3），显式实现调不到。
- `tests/PicoSerDe.Integration.Tests/TokenReaderConformanceTests.cs`：新增一个 `where TReader : IFusedTokenReader, allows ref struct` 的泛型辅助（口径同既有 `:37`/`:53`/`:78`），五个 reader 各穿一遍。

**怎么验证**
```bash
dotnet build PicoSerDe.slnx -c Debug -m:1
dotnet test PicoSerDe.slnx -c Debug --no-progress          # 0 failed
git status --short api/                                    # 必须为空
grep -rn "TokenReaderFused.TryRead" --include=*.cs PicoJetson/src PicoMsgPack/src PicoToml/src PicoYaml/src PicoIni/src | wc -l   # 期望 20（5 reader × 4 融合读）
```

**验收标准**（全部可判定）
1. 构建 0 warning / 0 error；全量测试 0 failed。
2. 五个 reader 经 `IFusedTokenReader` 见证：`HasCountedCollections == false`、`TryGetCollectionCount(out var c)` 返回 `false` 且 `c == 0`（此时谁都还没优化；这条同时是 spec §6 S4 的验收）。
3. `TryReadInt32(out v)` 与「`Read()` 后 `TryGetInt32(out v)`」在正整数输入上 `(true, v)` 相等；**类型不符**的 token 上返回 `false` 且 `TokenType` 仍为**该 token**（spec §3.2.2 (c)）；`Read()` 在输入尾失败后 `TryReadInt32` 同样 `false` 且 `NeedsMoreData` 与参考路径一致（spec §3.2.2 (b)）。
4. **转发是唯一实现（评审门）**：五个 reader 里这四个融合读的成员体必须是 `=> TokenReaderFused.TryReadX(ref this, …)`；出现**第二份手写** `Read()`+`TryGetX()` 组合即退回（spec §8 **RK-8** ③）。
5. `git status --short api/` 为空（spec §4.1 A-4）。

**依赖**：T1（接口与正本必须先存在）。
**回退**：`git revert <T1b>`（五个 reader 的 6 个成员 + 测试段）；T5 起依赖它，逆序回退。
**预算**：~120 行（30 个成员体，几乎每行一个）。

---

### T2 — `api/PENDING-CHANGES.md` 登记 R-3（spec §4.2）

**改哪里**
- `api/PENDING-CHANGES.md`：在既有表格（表头 `:11`，末行 `| 4 | PicoMsgPack | ...` 在 `:16`）**追加第 5 行**，格式逐列照抄 spec §4.2 的 R-3 行：`| 5 | PicoSerDe.Core | <新接口 + 6 个成员 + 新静态类的 mdoc 签名> | **R-3** | <理由> |`。
- 签名串必须与 spec §4.2 一致：`T:PicoSerDe.Core.IFusedTokenReader`、`P:PicoSerDe.Core.IFusedTokenReader.HasCountedCollections`、`M:PicoSerDe.Core.IFusedTokenReader.TryGetCollectionCount(out int count)`、`M:…TryReadInt32(out int value)`、`M:…TryReadInt64(out long value)`、`M:…TryReadFloat64(out double value)`、`M:…TryReadBool(out bool value)`、`T:PicoSerDe.Core.TokenReaderFused` + 4 个静态泛型方法。**静态泛型方法的 `ref` 首参与泛型元数在 dump 里的确切拼法未实测**（spec §9 **U4**）⇒ 先跑一次 dump 脚本、按**脚本实际输出**写入台账，不许照抄猜出来的形状。

**怎么验证**
```bash
git diff api/                       # 只能有 +1 行（表格第 5 行），不能有任何 - 行
grep -c "^| 5 | PicoSerDe.Core" api/PENDING-CHANGES.md   # 1
git status --short -- ':!api/PENDING-CHANGES.md'        # 必须为空（本任务不许碰别的文件）
```

**验收标准**
1. `git diff api/` 的 `-` 行数为 **0**（纯追加）。
2. 新增行含 `R-3` 字样；类型/成员串与 spec §4.2 一致，其中泛型方法行以 **dump 脚本输出**为准（若与 spec 的写法有差异，把差异写进 report —— 这就是 §9 U4 的证实动作）。
3. **`api/*.public.txt` 仍然零 diff**（本任务只改 `PENDING-CHANGES.md`，spec §4.1 A-4/A-5）。
4. 不新增「Registered capability change」段落 —— F-1/F-2 的登记属于 T13/T17（spec §4.3 说明它们是**行为修正**、不是新增，走 `api/PENDING-CHANGES.md:18-30` 的既有体裁）。

**依赖**：T1（先有成员，才能登记成员）。
**回退**：`git revert <T2>`（单行删除）。
**预算**：~10 行（一行表格，长行）。

---

### T3 — MsgPack「参考 vs 融合」双走 dump 护栏（spec §5.2、§6 S0）

**目的**：在**任何覆写都还没写**的时候，先把护栏立起来并**证明它有效**。这份护栏此后是 T5–T11 的唯一准入证明。

**改哪里**
- `PicoMsgPack/tests/PicoMsgPack.Tests/MsgPackSpanTokenDump.cs`：新增**两个**走法（与既有 `DrainSpan():360` / `DrainSequence():379` 并列，复用同一个 `Capture():434`）：
  - `DrainSpanFusedReference` —— 就是今天 `DrainSpan` 的字面行为（`Read()` + `Capture`）。
  - `DrainSpanFused` —— 融合走法：维护一个「剩余计数栈」，每个 Start token 之后调 `TryGetCollectionCount(out n)`（本任务里五个 reader 的成员还是 **T1b 的一行转发** ⇒ 恒返回 `false`、退回 `Read()` 驱动），元素体按参考侧 `TokenType` 选 `TryReadX`（spec §5.2 增量 + 本计划 §3.3 第 5 条）。**本任务的对拍证明的是「护栏 + 转发通道」有效**（spec §5.2 末段明确：真正的等价性证明落在 T5–T8/T14/T18 的优化成员上）。
- 新测试文件 `PicoMsgPack/tests/PicoMsgPack.Tests/MsgPackReaderFusedEquivalenceTests.cs`：对 `Payloads()` `:44`、`RandomPayloads()` `:183`、`MalformedPayloads()` `:160` 各跑两条走法并逐项比较。
- **不改** `.superpowers/sdd/perf-mp-readspan/harness/DumpTokens/Program.cs`：它是 4e 的跨构建 golden 工具，属历史留证（且该目录在 `.gitignore:415` 下、未入库 ⇒ 不是可依赖的基线）；本任务的对拍是**同一构建内**的自比对（spec §5.2 明确要求「不依赖历史日志、也不会腐化」）。

**怎么验证**
```bash
dotnet build PicoSerDe.slnx -c Debug -m:1
dotnet test PicoSerDe.slnx -c Debug --no-progress --filter "FullyQualifiedName~MsgPackReaderFusedEquivalenceTests"   # 0 failed
dotnet test PicoSerDe.slnx -c Debug --no-progress          # 全量 0 failed
```
**护栏有效性的负控（必做，证据进 report）**：在**本地未提交**状态下，对 `ITokenReader.TryReadInt32` 的默认体做一处单行破坏（把 `if (!Read())` 改成 `if (true)` 或删除 `Read()` 调用），重跑上述过滤测试，**必须变红**；然后 `git checkout -- PicoSerDe.Core/src/ITokenReader.cs` 还原。spec §6 S0 的验收原文就是「先证明护栏本身有效」。

**验收标准**
1. 三条语料（命名 / 随机 / 畸形）上，参考侧与融合侧的 `MsgPackTokenSnapshot` 列表**逐项相等**，且**行数相等**。
2. 负控变红、还原后变绿（两次输出都贴进 report）。
3. 全量测试 0 failed；`git status --short api/` 为空。
4. **不改任何 reader 实现**（`git diff --stat` 只许出现 `PicoMsgPack/tests/` 下两个文件）。

**依赖**：T1b（融合臂必须是 reader 自己实现的成员；只有 T1 的正本还不够 —— 本任务要打的是 `reader.TryReadX(...)` 这条路）。
**闸门**：**G-0t**（本任务只改 `PicoMsgPack/tests/`；开工前 `git -C .worktrees/mp-collection status --short PicoMsgPack/tests/` 必须为空——本文写作时实测为空）。
**回退**：`git revert <T3>`（两个测试侧文件，零生产代码）。
**预算**：~190 行。**若超 200 行**：按 T3a（两个 `Drain*` 走法，≤100 行）/ T3b（测试文件，≤100 行）切开。

---

### T4 — per-token 微基准闸门（spec §9 U2）

**目的**：§1 的停止规则需要「per-token 成本」这个量，而 spec §9 U2/U3 明确它**今天是算术推得的**，并给出证实方法：「同一 reader、10⁶ 次 `Read()`+`TryGetInt32`」。没有这个测点，RK-6 的停止规则无法执行。

**改哪里**
- 新增 harness `PicoMsgPack/tools/pertoken/`：**已跟踪**路径（照 `PicoMsgPack/tools/readspan/` 的既有工具约定，含 README），独立 console 工程，`<Compile Include>` 链接 `MsgPackSpanTokenDump.cs` 所在的共享源。**定义有别于 `.superpowers/`**：`.gitignore:415` 会吃掉 `.superpowers/` 下的任何东西，测点工具放那里等于下一个人拿不到。测三件事：
  1. `Read()` 单独（107 token 的 `Collection` 形状 payload，spec §1.2 已给出精确 token 账本）；
  2. `Read() + TryGetInt32` 各 10⁶ 次；
  3. `TryReadInt32` 10⁶ 次。
- 输出中位 ns/token（**不用**平均，README `:137` 要求中位 + 离散度）。

**怎么验证**
```bash
dotnet run -c Release --project PicoMsgPack/tools/pertoken      # 三轮，每轮独立进程
```
**验收标准**
1. 能在**不启动 bench 套件**的情况下给出「ns/token」三个数字，且三轮中位可复现（三轮间极差 ≤ 10%，否则判为噪声，加注解重跑）。
2. 与 spec §1.2 的 **13.4 ns/token**（算术推得，spec §9 U2 明确未实测）对比，**只记录差值、不下结论**；若差出 2 倍以上，在 report 里写明「spec §1.2 的分母假设需要在 S1 重估」。
3. 该 harness **入库但不进 `PicoSerDe.slnx`**：入库是为了收益数字可被复核，不进 slnx 是为了不拖慢 CI（slnx 已有 33 个 `Project`，benchmarks 均在其中，再加一个只为测量的工程无收益）。断言：`git ls-files PicoMsgPack/tools/pertoken` 非空，且 `grep pertoken PicoSerDe.slnx` **零命中**。

**依赖**：T1。**不依赖 G-0**（只读现有 reader，不改代码）。
**回退**：`git revert <T4>`（纯新增 harness 目录，零生产代码）。
**预算**：~120 行。

---

### T5 — MsgPack `HasCountedCollections` + `TryGetCollectionCount`（spec §6 S1，count 部分）

**改哪里**
- `PicoMsgPack/src/PicoMsgPack/MsgPackReader.cs`：在 `TryGetInt32` 族之后加（**把 T1b 的转发体换成真实实现**）
  `public readonly bool HasCountedCollections => true;` 与
  `public readonly bool TryGetCollectionCount(out int count)`，语义**逐字照 spec §3.3 的 MsgPack 行**：
  `_isMapStack[_depth-1] ? _elementStack[_depth-1] / 2 : _elementStack[_depth-1]`；`_depth == 0` 或当前 token 不是 Start ⇒ `false` + `count = 0`。
  **零状态变化**（spec §3.1：不前进、不改 `TokenType`/`Depth`/`NeedsMoreData`、不失效 span）。
- `tests/PicoSerDe.Integration.Tests/TokenReaderConformanceTests.cs`：把 MsgPack 那一行的 `HasCountedCollections == false` 断言改成 `true`（四个文本格式保持 `false`）。
- 新测试（`PicoMsgPack/tests/PicoMsgPack.Tests/` 下）：count 矩阵。

**怎么验证**
```bash
dotnet build PicoSerDe.slnx -c Debug -m:1
dotnet test PicoSerDe.slnx -c Debug --no-progress          # 0 failed
dotnet run -c Release --project PicoMsgPack/tools/pertoken   # 未变（本任务不改热路径）
```
**验收标准**（spec §5.3「集合计数」行逐条落地）
1. count = **0 / 1 / 15 / 16 / 255 / 256 / 65535 / 65536** 各有一个 payload，`TryGetCollectionCount` 返回值等于该集合声明的大小。
2. **map 奇偶残留**：在 key 位置与 value 位置分别查询 ⇒ 返回的是「剩余**完整对数**」（`_elementStack / 2`，向下取整）；特别覆盖 `_elementStack` 为奇数的中间态（spec RK-3 专打这个洞）。
3. **空集合** ⇒ `true` + `0`；**截断集合** ⇒ 不抛、返回剩余数（查询不是读，spec §3.1）。
4. `_depth == 0`、或当前 token 是标量/End ⇒ `false` + `count == 0`。
5. **零状态副作用**：查询前后 `TokenType` / `Depth` / `BytesConsumed` / `NeedsMoreData` 全等（用两次快照比对）。
6. **T3 的双走 dump 仍然 IDENTICAL**（本任务不改 token 流，行数也必须一字不差）。
7. 四个文本格式的 `HasCountedCollections == false` 与 `TryGetCollectionCount` 恒 `false` 断言仍绿（spec §5.3 最后一条）。

**依赖**：T3（护栏必须先存在）。
**闸门**：**G-0**。
**回退**：`git revert <T5>`。**注意**：T9 依赖本任务，回退 T5 必须同时回退 T9–T11。
**预算**：~90 行。

---

### T6 — MsgPack `TryReadInt32` 覆写（spec §6 S1）

**改哪里**
- `PicoMsgPack/src/PicoMsgPack/MsgPackReader.cs`：覆写 `public readonly bool TryReadInt32(out int value)`，**形状逐字照 spec §3.3 的伪代码**：
  - `_isSequence` ⇒ 就地 `return TokenReaderFused.TryReadInt32(ref this, out value);`（退回参考正本，spec §2.2 N4：融合只做 span 快路径）；
  - `_depth > 0 && _elementStack[_depth-1] == 0`（结束 token 路径，`:375`）⇒ 退回参考正本；
  - `_position >= _data.Length` ⇒ `_needsMoreData = !_isFinalBlock; value = 0; return false`（与 `Read()` 一致，spec §3.3 伪代码）；
  - 一次 `switch (_data[_position])`：int 家族（fixint / negfixint / `0xCC`–`0xD3`）→ 边解码边前进，**且必须物化** `_tokenType` / `_singleByte` / `_valueSpan` 并调 `CountElement()`（spec §3.2.3：「融合 ≠ 不物化状态」）；
  - `default:` ⇒ **退回参考正本** `TokenReaderFused.TryReadInt32(ref this, out value)`（这是等价性的关键：非 int token 不做任何特殊处理，与正本逐字同路）。

**怎么验证**
```bash
dotnet build PicoSerDe.slnx -c Debug -m:1
dotnet test PicoSerDe.slnx -c Debug --no-progress --filter "FullyQualifiedName~MsgPackReaderFusedEquivalenceTests"   # IDENTICAL
dotnet test PicoSerDe.slnx -c Debug --no-progress          # 全量 0 failed
dotnet run -c Release --project PicoMsgPack/tools/pertoken
dotnet run -c Release --project PicoMsgPack/benchmarks/PicoMsgPack.Benchmarks --no-launch-profile   # 三轮
```
**验收标准**
1. T3 的双走 dump 在**全部入口**（`SPAN` / `SEQ` / `SPAN-NONFINAL` / `SEQ-NONFINAL` × chunk 1/2/3/5/7，spec §5.3「流式分块」行）上 **IDENTICAL 且行数相等**。
2. spec §5.3「结果」行的四类失败逐条覆盖并等价：**类型不符**（`false` 且 token 保持 current，后续 `TryGetX` 仍可用）、**溢出**（`int64`/`uint64` 越界 ⇒ `false`，状态与参考一致）、**截断**（`NeedsMoreData` 与参考一致）、**畸形**（异常类型与消息逐字相同）。
3. spec §5.3「token 家族」行的 int 家族全覆盖：fixint / negfixint / uint8·16·32·64 / int8·16·32·64。
4. spec §5.3「深度」行：`MaxDepth = 64`（`MsgPackReader.cs:35`）边界，越过一层抛**同类型同消息**（`MsgPackReader.cs:235-236`）。
5. 全量测试 0 failed；`git status --short api/` 为空。
6. **§1 停止规则**：预注册预期 = spec §9 U1 的下限 **30%** per-token 成本回收 ⇒ 阈值 **15%**。实测 < 15% ⇒ **停在这里**并报告（不再做 T7/T8/T9/T11），把 T1–T6 作为独立成果保留（spec RK-6）。
7. `Collection — Deserialize` 按协议（三轮取中位）**如实重测并公布**，**不设目标值**（spec §6 S1 原文）。

**依赖**：T5。
**闸门**：**G-0**。若 G-0c 判定「mp-collection 已消掉 int 二次分派」⇒ 本任务**跳过**，把 `TryReadInt32` 的覆写降级为「只覆写 `HasCountedCollections` 路径之外的部分」或直接不做，理由写进 report。
**回退**：`git revert <T6>`（单个成员块；T7/T8/T9/T11 依赖它的 `switch` 骨架，需一并回退）。
**预算**：~150 行。

---

### T7 — MsgPack `TryReadInt64` 覆写（spec §6 S1）

**改哪里**：`MsgPackReader.cs`，`TryReadInt64` 覆写。**复用 T6 建立的 `switch` 骨架**（int 家族的超集 + `0xCF` uint64 的越界判定），失败分支一律退回参考正本（`TokenReaderFused.TryReadInt64(ref this, out value)`）。

**怎么验证 / 验收标准**：同 T6 的 1–5、6–7 条，其中：
- 额外的溢出断言：`uint64` 值 > `long.MaxValue` ⇒ `false`（与 `TryGetInt64` 今天的判定一致）。
- per-token 测点用 `TryReadInt64`。

**依赖**：T6（同一 `switch` 骨架；T6 若按 G-0c 跳过，本任务改为独立建立骨架）。
**闸门**：**G-0**。
**回退**：`git revert <T7>`。
**预算**：~90 行。

---

### T8 — MsgPack `TryReadFloat64` / `TryReadBool` 覆写（spec §6 S1）

**改哪里**：`MsgPackReader.cs`，两个覆写：float32（`0xCA`）/ float64（`0xCB`）；bool（`0xC2`/`0xC3`）。同样「成功分支一次分派、`default:` 退回参考正本、必须物化状态」。

**怎么验证 / 验收标准**：同 T6 的 1–5 条，另加：
- float 按**位**比较（`BitConverter.DoubleToInt64Bits`，与 `Capture():434` 的 `X16` 打印一致）⇒ 覆盖 `NaN` / `±Inf` / `-0.0` 三个位模式。
- 溢出/类型不符：float32 token 上 `TryReadInt32` 必须 `false`（str 家族同理）。

**依赖**：T6。
**闸门**：**G-0**。
**回退**：`git revert <T8>`。
**预算**：~110 行。

---

### T9 — 生成器 **E-1** 计数循环（spec §6 S1、spec §3.4.1）

**改哪里**
- 新增 `shared/Gen/FusedEmit.cs`（照 `shared/Gen/FormatCapabilities.cs:24-30` 的两条纪律：**五处共用 ⇒ 必须 `internal`**；`api/*.public.txt` 不得变化 ⇒ spec §3.4.2 与 A-4）。
  - 签名必须把 **reader 变量名**与 **reader 类型名**当作参数（spec §3.4.2 C-1：JSON/MsgPack/INI 生成 `reader`，TOML 生成 `r`，YAML 两种都有 ⇒ 不能像 `GenInfrastructure.EmitCollectionFromList`（`GenInfrastructure.cs:1243`）那样只收 `kind/listVar/assignTarget`）。
  - `netstandard2.0` 约束（spec §3.4.2 C-3）：不引入运行期依赖。
- `PicoMsgPack/src/PicoMsgPack.Gen/MsgPackSerializerGenerator.cs`：**只接**形状相同的标量元素站点，**只接 MsgPack**（其余格式在各自任务里接）。spec §9 **U8** 明确「24 处发射点里有多少最终能被共享发射器覆盖」**未逐点判定** ⇒ 本任务**不许**声称覆盖率：开工前把逐点清单（10 个 `TokenType.ArrayEnd` 站点：`:626,733,1107,1391,2929,2973,3005,3108,3462,3517`）写进 report，逐点判「接 / 不接 / 待定」；**不接**的必须写明理由。
- `PicoMsgPack/src/PicoMsgPack.Gen/PicoMsgPack.Gen.csproj`：`<Compile Include>` 新共享文件。
- `tests/FormatCapabilities.Probe/generated-baseline/PicoMsgPack.Gen/`：按 spec §3.4.2 **C-4**「生成代码基线必须由生成器重新生成，不手工合并」刷新。

**怎么验证**
```bash
dotnet build PicoSerDe.slnx -c Debug -m:1
dotnet test PicoSerDe.slnx -c Debug --no-progress          # 0 failed（含生成器 baseline 测试）
dotnet test PicoSerDe.slnx -c Debug --no-progress --filter "FullyQualifiedName~MsgPackReaderFusedEquivalenceTests"
dotnet run -c Release --project PicoMsgPack/benchmarks/PicoMsgPack.Benchmarks --no-launch-profile
git diff --stat tests/FormatCapabilities.Probe/generated-baseline/
```
**验收标准**
1. spec §3.4.1 **E-1** 的形状逐字落地：`Read()` 拿到 Start 后 `for (var __i = 0; __i < __n; __i++) { … }`，再**显式一次** `reader.Read()` 消费结束 token。
2. **`Read()` 次数相等**（spec §3.4.1 的论证 + §5.3「生成器侧」行）：E-1 发出的 `Read()` 次数 = `count + 1`，与 E-2 的 `while` 形式完全一致 ⇒ T3 的 dump 行数**不变**（这就是判据，不另建计数桩）。
3. `generated-baseline/` 重生成后：**wire 输出不变**（跑跨格式集成测试与 cross-validation 全绿）；golden diff 里只有集合循环形状的变化，人工逐块确认。
4. 全量 0 failed；`git status --short api/` 为空。
5. **§1 停止规则**：预注册预期取自 spec §9 **U5** ——「E-1 单独能省多少**未测**，spec §3.4.1 已论证它只省每元素的 `_tokenType` 比较」。因此本任务的预期**不设百分比**，只要求**可分辨**：若三轮中位与基线差在小数第三位以下（噪声内）⇒ 标记「E-1 无独立收益」，不阻塞 T11（收益预期在 E-3，spec §3.4.1 末句、§9 U5），但要在 report 写清。

**依赖**：T5（`TryGetCollectionCount` 是 E-1 的 `__n` 来源）。
**闸门**：**G-0**（spec RK-5 缓解③：本任务的发射点与 mp-collection 的 `Collection` 快路径重叠）。
**回退**：`git revert <T9>` + 重新生成 baseline（`git revert` 后 baseline 会自动回到一致，因为 baseline 也在同一 commit 里）。
**预算**：~150 行（不含 baseline 重生成）。

---

### T10 — 生成器护栏：产物不含 `ITokenReader`（spec §3.4.3）

**改哪里**
- 新增测试（`tests/PicoSerDe.Integration.Tests/` 或生成器测试工程）：对 `tests/FormatCapabilities.Probe/generated-baseline/` 全量文本做结构性 grep 断言，**不得出现 `ITokenReader` 字样**；同时对生成器源码的**发射字符串**做同样断言（防止基线还没重生成的窗口里漏检）。

**怎么验证**
```bash
dotnet test PicoSerDe.slnx -c Debug --no-progress --filter "FullyQualifiedName~ITokenReader"
grep -rn "ITokenReader" tests/FormatCapabilities.Probe/generated-baseline/ ; echo "exit=$?"   # 期望无命中
```

**验收标准**
1. 基线目录 `grep ITokenReader` **零命中**。
2. 抗漂移自检：临时把某个发射点改成 `((ITokenReader)reader).TryReadInt32(...)`（本地不提交）⇒ 测试**必须变红**，还原后变绿；两次输出进 report。
3. 全量 0 failed。

**依赖**：T9（第一个把 reader 成员当融合调用发射出去的任务）。
**闸门**：**G-0**。
**回退**：`git revert <T10>`（一个测试文件）。
**预算**：~50 行。

---

### T11 — 生成器 **E-3** 融合元素读（spec §6 S1、spec §3.4.1）

**改哪里**
- `shared/Gen/FusedEmit.cs`：加 E-3 发射（`Read()` + `TryGetX()` → `TryReadX()`）。
- `MsgPackSerializerGenerator.cs`：在 T9 已接的站点上把标量元素体换成 `reader.TryReadInt32(...)` 等（spec §3.4.1 E-3）。
  - **失败判据不对称（spec §3.4.1 末段，不许抹平）**：E-1（计数循环）下 `false` 只能是类型不符 ⇒ 立即 `throw`；E-2（结束符循环）下 `false` 还可能是「到 `]` 了」⇒ 先判 `TokenType` 是否结束、否则 `throw`。本任务只做 E-1 侧（MsgPack 有 count）。
- `tests/FormatCapabilities.Probe/generated-baseline/PicoMsgPack.Gen/`：重新生成。

**怎么验证**
```bash
dotnet build PicoSerDe.slnx -c Debug -m:1
dotnet test PicoSerDe.slnx -c Debug --no-progress          # 0 failed
dotnet run -c Release --project PicoMsgPack/benchmarks/PicoMsgPack.Benchmarks --no-launch-profile   # 三轮
dotnet run -c Release --project PicoMsgPack/tools/pertoken
```
**验收标准**
1. 生成代码里出现 `reader.TryReadInt32(` / `TryReadInt64(` / `TryReadFloat64(` / `TryReadBool(`，且 `reader` 是**具体类型**的局部变量（spec §3.4.3：不许走接口）。
2. **T10 的护栏仍绿**（产物零 `ITokenReader`）。
3. `Collection — Deserialize` 与 `MTenInts` 按协议**如实重测并公布**（spec §6 S1：不设目标值）。
4. **§1 停止规则**：E-1（T9）与 E-3（T11）**必须各测一次**（spec §9 U5 的证实方法原文：「把 E-1 与 E-3 拆成两次独立提交，各测一次」）。预注册预期 = spec §9 U1 下限 30% ⇒ 阈值 15%：若 **T9+T11 累计** per-token 回收 < 15% ⇒ **停在 T11，T12 之后全部转欠账**，报告里明确写「契约（T1–T2）保留为独立成果」（spec RK-6）。
5. 全量 0 failed；`git status --short api/` 为空。
6. **spec §9 U6 的记录义务**：report 必须回答「端到端 `Collection — Deserialize` 变化多少」，并注明该模型 107 个 token 里 100 个是逐元素解码、MemoryPack 的「整数组一次 memcpy」在这里拿不到（spec §9 U6）—— 只记录，不解释成设计缺陷。

**依赖**：T9、T6（或 T7/T8，取决于生成器发射哪个标量族）。
**闸门**：**G-0**。
**回退**：`git revert <T11>` + baseline 同 commit 回退。
**预算**：~130 行（不含 baseline 重生成）。

---

### T12 — JSON「参考 vs 融合」双走 dump 护栏（新建语料，spec §6 S2）

**改哪里**
- 新增 `PicoJetson/tests/…/JsonSpanTokenDump.cs`：照 `MsgPackSpanTokenDump.cs` 的形状（`Payloads()` / `RandomPayloads(seed)` / `MalformedPayloads()` / `SmallPayloads()` / `Capture(...)` / `Drain*`），**按 JSON 的 token 家族**取 payload，而不是照抄 MsgPack 的字节。
- 新增双走测试：参考侧（`Read()` + 全部 getter）与融合侧（按参考侧 `TokenType` 选 `TryReadX`）逐项比对。
- `PicoJetson.Gen` 不参与本任务（本任务零生产代码）。

**为什么必须先有它**：spec §5.2 原文「没有对拍就不许改」是本规格唯一的准入条件；T13 要**删**一段现有公开行为（F-1），删它之前必须先有能证明「删前删后语义正确」的护栏。

**怎么验证**
```bash
dotnet build PicoSerDe.slnx -c Debug -m:1
dotnet test PicoSerDe.slnx -c Debug --no-progress --filter "FullyQualifiedName~JsonSpanTokenDump|FullyQualifiedName~JsonReaderFusedEquivalence"
dotnet test PicoSerDe.slnx -c Debug --no-progress          # 全量 0 failed
```
**验收标准**
1. 参考侧与融合侧（此时融合侧全是 T1b 的**一行转发**）**逐项相等、行数相等**。
2. 负控：临时破坏 `ITokenReader.TryReadInt32` 默认体 ⇒ 测试变红，还原变绿（同 T3）。
3. JSON 的 token 家族覆盖（spec §5.3 的 JSON 相关行）：对象/数组 Start·End、字符串（含 `\uXXXX` 与裸控制字符）、数字（正/负/0/大整数）、`true`/`false`/`null`、截断、畸形（spec 引 `JsonReader.cs:496-499`：`]` 是扫字节得出的，无元素计数）。
4. 零生产代码改动（`git diff --stat` 只许 `PicoJetson/tests/`）。
5. 全量 0 failed。

**依赖**：T1b（融合臂 = reader 自己实现的成员）。
**闸门**：无（不碰 MsgPack）。
**回退**：`git revert <T12>`（纯新增测试侧文件）。
**预算**：~190 行。**若超 200 行** ⇒ 拆 T12a（payload 矩阵）/ T12b（双走 walker）。

---

### T13 — 删除 JSON `TryReadNextInt32Seq`（**F-1**）+ 负数回归（spec §6 S2、spec §4.3）

**改哪里**
- `PicoJetson/src/PicoJetson/JsonReader.cs`：
  - `TryReadNextInt32`（`:656`）的分流（`:658` 的 `_isSequence ? TryReadNextInt32Seq(...) : ...`）改为**序列一律退回参考正本**（`TokenReaderFused.TryReadInt32(ref this, out value)`；spec §6 S2 原文「删除 seq 融合分支」；spec §4.3「该路径**必须**被删除或改正」）。
  - 删除 `TryReadNextInt32Seq`（`:771`）及其独占 helper；**`TryReadNextInt32Span`（`:725`）逐字保留**。
- 新回归用例（spec §6 S2 验收原文「F-1 的回归用例（负数的 span 与 seq 两条）进仓库」）：`[-2]` 在 span 与 seq 两种构造下都必须得到 **−2**（今天是 span 对、seq 返回 2：`JsonReader.cs:789-790` `Advance(1)` 吞掉 `-` 而不记负号，对比 `:743-747` span 路径有 `bool neg`）。
- `api/PicoJetson.public.txt` **不动**（签名不变，spec §4.3）；在 `api/PENDING-CHANGES.md` 的「Registered capability change」体裁下补一条（照 `:18-30` 的既有段落：签名不变、返回值变正确、属 `x+1` 轴）。

**怎么验证**
```bash
dotnet build PicoSerDe.slnx -c Debug -m:1
dotnet test PicoSerDe.slnx -c Debug --no-progress          # 0 failed
git status --short api/                                    # 只许 PENDING-CHANGES.md
```
**验收标准**
1. 负数的 span 与 seq 两条用例都得到**正确的负值**（这就是 F-1 修好的判据）。
2. `TryReadNextInt32` 的**签名不变**（`api/PicoJetson.public.txt:81` 零 diff），只有**返回值变正确**。
3. T12 的双走 dump 仍 **IDENTICAL**（删的是 seq 融合分支，参考路径不变 ⇒ dump 不该变；若变了，说明删错了）。
4. 全量 0 failed。
5. spec §9 **U7** 记录义务：开工前 `grep -rn "TryReadNextInt32" --include=*.cs .` 清点调用面（spec §9 U7 已查：**0 处非测试调用**），把结果贴进 report；若出现非测试调用 ⇒ **停下报告**（那是 breaking change，超出本任务）。

**依赖**：T12（必须先有护栏）。
**回退**：`git revert <T13>`。
**预算**：~130 行。

---

### T14 — JSON `TryReadInt32` 覆写（spec §6 S2）

**改哪里**
- `PicoJetson/src/PicoJetson/JsonReader.cs`：覆写 `TryReadInt32`。spec §3.3 对 JSON 的裁定是「**`TryReadInt32` 覆写，其余按需**；先修 F-1/F-3」⇒ 本任务只做 `TryReadInt32`。
  - 一次 `switch` 完成「分类 + 取值」：数字起始字节判定 → span 解析 → **物化 `_tokenType` / `_valueSpan` / `_tokenValueStart/End`**（spec §3.2.3：融合 ≠ 不物化状态；F-3 正是覆写必须避免的偏离，spec §1.4 引 `JsonReader.cs:725-768` 全文无 `_tokenType` 赋值）。
  - `_isSequence` ⇒ 退回参考正本；非 int ⇒ 退回参考正本。
  - **不许继承 F-4 的不一致**（spec §1.4 F-4：`TryReadNextInt32` 遇 `]`/`}` 不动（`:737-742`）、`TryReadInt32ArrayFast` 遇 `]` 会吃掉（`:1978-1981`））⇒ 覆写只在「当前字节是数字」时行动，否则一律退回参考正本。

**怎么验证**
```bash
dotnet build PicoSerDe.slnx -c Debug -m:1
dotnet test PicoSerDe.slnx -c Debug --no-progress --filter "FullyQualifiedName~JsonReaderFusedEquivalence"   # IDENTICAL
dotnet test PicoSerDe.slnx -c Debug --no-progress          # 全量 0 failed
dotnet run -c Release --project PicoJetson/benchmarks/PicoJetson.Benchmarks --no-launch-profile
```
**验收标准**
1. T12 的双走 dump 在 span / seq / NonFinal 全档 **IDENTICAL**、行数相等。
2. **F-3 专项断言**：融合读之后 `TokenType`、`BytesConsumed`、`Depth`、以及 `TryGetInt64`/`TryGetFloat64`/`TryGetBool`/字符串原始内容**全部**与参考路径相等（spec §5.1 的「全部 getter」列 —— 这是本任务最容易失手的一列）。
3. **F-4 专项断言**：当前字节是 `]`/`}` 时 `TryReadInt32` 返回 `false` 且**位置不动**（`BytesConsumed` 不变）。
4. 全量 0 failed；`git status --short api/` 为空。
5. **§1 停止规则**：per-token 回收 < 15% ⇒ 停在 T14，T15–T18 转欠账。

**依赖**：T13。
**回退**：`git revert <T14>`。
**预算**：~150 行。

---

### T15 — JSON **E-3** 发射（条件任务，spec §6 S2）

**触发条件**：T11 之后 MsgPack 的实测收益 ≥ 阈值（§1），且 T14 的 per-token 回收 ≥ 15%。否则**不启动**，记为欠账。

**改哪里**
- `PicoJetson/src/PicoJetson.Gen/JsonSerializerGenerator.cs`：JSON 的 10 个 `TokenType.ArrayEnd` 站点（spec §3.4.2 C-2 列出：`:1638,3162,3678,3690,4543,5144,6176,6207,6542,6970`）里**形状相同**的标量元素站点换成 `TryReadInt32`；**字典的 `PropertyName` 循环与带判定逻辑的站点留在原处**（spec §3.4.2 C-2 末句）。
- JSON `HasCountedCollections == false` ⇒ **只做 E-3，不做 E-1**（spec §3.3）。失败判据用 **E-2 分支**（先判 `TokenType` 是否结束、否则 throw，spec §3.4.1 末段）。
- baseline 重新生成。

**怎么验证 / 验收标准**：同 T11 的 1–4 条（把 MsgPack 换成 JSON 的 10 个站点清单，逐点判「接 / 不接 / 待定」，理由写进 report —— spec §9 **U8** 要求逐点判定，不许声称覆盖率）。

**依赖**：T14。
**回退**：`git revert <T15>` + baseline 同 commit。
**预算**：~120 行（不含 baseline 重生成）。

---

### T16 — TOML「参考 vs 融合」双走 dump 护栏（条件任务，spec §6 S3）

**触发条件**：T15 完成且 TOML 未被判为欠账。spec §6 S3 明确**允许停在 S2 并把 TOML 记为欠账**，reopen 条件是「TOML 读侧成为瓶颈时」。

**改哪里**：新增 TOML 语料 + 双走测试（复用 T12 的走法形状）。TOML 的 token 家族按 `TomlReader` 实际发射的种类取；**必须覆盖负数**（F-2 的洞就在负数上，spec §1.4 F-2 引 `TomlReader.cs:686-711`）。

**验收标准**：同 T12 的 1–5 条；额外要求负数 payload 必须在语料里（否则 T17 无可证）。

**依赖**：T12（走法形状）。
**回退**：`git revert <T16>`。
**预算**：~190 行。超预算则拆 T16a/T16b（spec §3.2 成本置信度低）。

---

### T17 — TOML **F-2** 重做（条件任务，spec §6 S3、spec §4.3）

**改哪里**
- `PicoToml/src/PicoToml/TomlReader.cs`：`TryReadNextInt32`（`:635`）按**参考正本语义**重做 —— `TryReadNextInt32Seq`（`:686`，F-2：不处理 `-`，负数直接 `false`）**删除**，seq 一律退回参考正本；`TryReadNextInt32Span`（`:642`）保留并修负数。
- `api/PENDING-CHANGES.md`：按「Registered capability change」体裁补一条（**签名不变、返回值变正确**，spec §4.3）。

**验收标准**：同 T13 的 1–5 条（把 JSON 换成 TOML；`api/PicoToml.public.txt:44` 零 diff）。

**依赖**：T16。
**回退**：`git revert <T17>`。
**预算**：~140 行。

---

### T18 — TOML `TryReadInt32` 覆写 + E-3（条件任务，spec §6 S3）

**改哪里**：`TomlReader.cs` 覆写 `TryReadInt32`（spec §3.3：TOML「按需（排第三）」，有 ArrayStart/End（`TomlReader.cs:1285,1319`）但**无 count** ⇒ 只做 E-3、不做 E-1）+ `TomlSerializerGenerator.cs` 的 4 个 `TokenType.ArrayEnd` 站点（spec §3.4.2 C-2：`:1632,2150,2834,3555`）里的标量元素站点。
**不许动**：带 `TablePath` 判定的站点（spec §3.4.2 C-2 明确 `TomlSerializerGenerator.cs:1617` 留在原处）；`TryReadInt32ArrayFast`（`:2071`）除非本任务明确包含 —— **本任务不含**。

**验收标准**：同 T14 + T15 的组合。

**依赖**：T17。
**回退**：`git revert <T18>` + baseline 同 commit。
**预算**：~140 行。

---

## 6. 不做的事（明确列出，逐条追 spec）

| # | 不做 | 依据 |
|---|---|---|
| 1 | **不改任何 wire 格式** | spec §2.2 **N1** |
| 2 | **不改 `Read()` 的对外语义**（`Skip()`/`TokenType`/`BytesConsumed`/`IsResumed`/`ExportState` 依赖它今天的形状） | spec §2.2 **N2** |
| 3 | **不做 MemoryPack 那种「无 tag 定长格式」** | spec §2.2 **N3**、§7 否决方案 2 |
| 4 | **不改流式（sequence）委托的形态**：融合只做 span 快路径，seq 一律一行转发参考正本 | spec §2.2 **N4**、§3.3 |
| 5 | **不在 reader 里加「计数作用域」去省掉 ③`CountElement()` 的两次栈写与 ④ 的结束符判定** | spec §2.2 **N5**、§7 否决方案 5（改 `_elementStack` 记账节奏 ⇒ 改可观测行为） |
| 6 | **不承诺任何性能数字** | spec §2.2 **N6**、§9 U1/U2/U3 |
| 7 | **不动 YAML / INI 的 reader**（没有可计数的集合形态；只有一致性断言） | spec §3.3、§6 **S4** |
| 8 | **不动其它格式的生成器**：`PicoYaml.Gen` / `PicoIni.Gen` / `PicoToml.Gen` 的发射点**只**在 T15（JSON）/ T18（TOML）明确包含时改动；YAML/INI 生成器**全程不动** | spec §6 S4、§3.4.2 C-2 |
| 9 | **不新增第七个融合成员**（`TryReadDateTime`/`TryReadDecimal` 等） | spec RK-7：新增融合读必须**先有 §5.2 的对拍语料** |
| 10 | **不许手改 `api/*.public.txt`**，也不许手合并 `generated-baseline/` | spec §4.1 **A-4**、§3.4.2 **C-4** |
| 11 | **不改 TOML 的 `TryReadInt32ArrayFast`（`:2071`）与 JSON 的 `TryReadInt32ArrayFast`（`:1959`）**，除非某任务的验收明确要求 | spec §3.3 未把它们列为覆写点 |
| 12 | **不把 `TryReadNextInt32` / `TryReadNextInt32Seq` 的分歧「统一成一个更聪明的实现」**：F-1/F-2 的处置是**删除 seq 分支**（seq 一行转发参考正本），不是补一个 seq 融合实现 | spec §4.3（「必须被删除或改正」）、§6 S2/S3 |

---

## 7. 每个任务的验证配方（同一个脚本，避免「凭感觉」）

```bash
# 0) 新 worktree 前置（否则 restore 直接 NU1301）
mkdir -p artifacts/nupkg

# 1) 构建（硬约束：0 warning / 0 error）
dotnet build PicoSerDe.slnx -c Debug -m:1

# 2) 全量测试（0 failed）
dotnet test PicoSerDe.slnx -c Debug --no-progress

# 3) 等价性（每个覆写任务：必须 IDENTICAL 且行数相等）
dotnet test PicoSerDe.slnx -c Debug --no-progress --filter "FullyQualifiedName~FusedEquivalence|FullyQualifiedName~SpanSequenceEquivalence"

# 4) 公共面（`api/*.public.txt` 永远为空；`PENDING-CHANGES.md` 只有 T2/T13/T17 可改）
git status --short 'api/*.public.txt'   # 必须为空
git status --short api/                # 除 T2/T13/T17 外必须为空

# 5) 生成面（发射任务）
git diff --stat tests/FormatCapabilities.Probe/generated-baseline/
grep -rn "ITokenReader" tests/FormatCapabilities.Probe/generated-baseline/   # 期望零命中

# 6) 性能（仅在带性能诉求的任务：T6–T11、T14、T15、T18；三轮中位；T9 只求可分辨不倒退）
dotnet run -c Release --project PicoMsgPack/tools/pertoken
dotnet run -c Release --project PicoMsgPack/benchmarks/PicoMsgPack.Benchmarks --no-launch-profile

# 7) 格式 + 提交
dotnet csharpier format
git add -A && git commit -m "…"
```

---

## 8. 执行顺序与停止点

```text
G-0（并行冲突闸门，§2）
  └─ T1（机制先证）→ T1b（五 reader 转发）→ T2 → T3 → T4 → T5 → T6 ──┬─ 停止点 A（§1：per-token 回收 < 15%）
                                                                     ├─ T7 → T8 → T9 → T10 → T11 ── 停止点 B（spec §6 S1 收口）
                                                                     │                              └─ T12 → T13 → T14 ──┬─ 停止点 C（spec §6 S2 收口）
                                                                     │                                                    ├─ T15
                                                                     │                                                    └─ T16 → T17 → T18 ── 停止点 D（spec §6 S3 收口）
```

**T1 是唯一带「前置证伪闸门」的任务**：P-1…P-4 四条判据（spec §R1）只要有一条与实测不符，整条链就在 T1 停下 —— 这比在 T6 发现机制不可行便宜得多（上一版就白跑了整轮 T1）。

- **停止点 A**：T6 起就没有可分辨收益 ⇒ 停在 T6，把 T1/T1b/T2–T6 保留为「契约 + MsgPack int 融合」的独立成果（spec RK-6 允许）。
- **停止点 B**：spec §6 S1 收口；`Collection — Deserialize` 与 `MTenInts` 已如实公布。
- **停止点 C**：spec §6 S2 收口；**spec §6 S3 明确允许停在这里**，把 TOML 记为欠账，reopen 条件「TOML 读侧成为瓶颈时」。
- **停止点 D**：全计划完成。

每个停止点都必须写进 `.superpowers/sdd/plan-fused/report.md`：停在哪、为什么停、哪些任务是欠账、reopen 条件是什么。

---

## 9. 交付报告（`.superpowers/sdd/plan-fused/report.md`）

每个任务完成后必须追加：任务编号、commit hash、`git diff --stat` 摘要、验证命令的**原始输出尾部**、性能数字（若适用，标「三轮中位 / 单轮 / 噪声内」）、是否触发停止规则、欠账清单。**性能数字只许写实测值**（spec §2.2 N6）。
