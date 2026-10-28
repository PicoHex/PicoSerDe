# 2026-10-28 性能改造轮次总结

> 本文件汇总 2026-10-27 ~ 10-28 这一轮针对 JSON / MsgPack 的性能改造、期间修掉的真实 bug、
> 以及**没有做成**和**仍然落后**的部分。原则：**每个数字都能追到来源**（报告章节或日志），
> 没测到就写"未测"，不美化。
>
> 方向约定：`×` = **我们 ÷ 竞品**（与基准工具打印的方向一致）。`> 1.00` 表示**我们更慢**，
> `< 1.00` 表示**我们更快**，全文不取倒数。
>
> 本文不记录测试数量：验证方式是跑 `dotnet test PicoSerDe.slnx -c Debug` 并判 `0 failed`。

## 1. 一句话结论

归因分析给出的**六条主因**全部落地（JSON 三条、MsgPack 三条），期间修掉**两个真实正确性 bug**
（MsgPack timestamp-64 字段序、strict 测试因按值复制 `ref struct` 而空转）与**两条时间敏感的
假阳性测试**。代价有两条，都写在这里：MsgPack 往返会把 `DateTimeKind` 归一为 `Utc`（规范固有），
以及 JSON 在 AOT 下仍有 9/12 场景慢于 System.Text.Json。

## 2. 改动总表

| # | 改了什么 | 为什么（归因） | commit | 实测改前 → 改后 | 证据 |
|---|---|---|---|---|---|
| 1 | JSON 读侧：属性名用**精确匹配索引**分派，替代逐属性大小写折叠 `Eq` 链 | §0-1（读侧 O(n) 名字匹配） | `5169116` | JIT `LargeFlat 50 DES` 6.120 / 6.132 µs（2.82× / 2.97×）→ 3.095 / 3.184 µs（1.47× / 1.42×），**−49%** | `.superpowers/sdd/perf-p01/report.md` §5.1 |
| 2 | JSON 写侧：属性名用**编译期 UTF-8 常量**写出；有命名策略时保留 `ConvertName` 原路径 | §0-3（每属性每调用重编码名字） | `583b551` | JIT `LargeFlat 50 SER` 中位 2.244 µs（2.06×）→ 1.874 µs（1.50×），**−16.5%** | `.superpowers/sdd/perf-p02/report.md` §5.1 |
| 3 | JSON 字符串：**SIMD 扫描** + 长串免二次拷贝；需转义的串走原逐字节路径 | §0-2（逐字节扫描 + 长串二次分配） | `4c5d107` | JIT `LargeString 1KB DES` 1.660 µs（5.71×）→ **0.261 µs（0.91×）**，**−84%**；`TenStrings100 DES` 2.557 → 1.323 µs（−48%） | `.superpowers/sdd/perf-p03/report.md` §0 |
| 4 | MsgPack：`timestamp64` 的**纳秒取高位、秒取低位**（规范位序） | 真实 bug（非性能） | `9e13e78` | 规范载荷（秒=1）此前会被解成 1992 年 | `.superpowers/sdd/perf-msgpack/analysis.md` §6.4 |
| 5 | MsgPack 写侧：writer **自持 span**，只在 span 用尽时触碰底层 buffer | §0-1（每 token 两次 `IBufferWriter` 调用） | `56d86e5` + `7a382a2` | 宽对象（15 字符串 + 15 int）写 **1126 → 528 ns（−53%）** | `.superpowers/sdd/perf-mp-span/report.md` |
| 6 | MsgPack 写侧：字符串**直接编码进 span**，不再 `Encoding.UTF8.GetBytes` 分配 `byte[]` | §0-4（每字符串一次分配） | `0a45e16` | 分配 **600 → 0 B/op**；边界串模型 1832 → 0；list/dict 模型 424 → 0；写路径 CPU **−31%** | `.superpowers/sdd/perf-mp-str/report.md` §4 |
| 7 | MsgPack：`DateTime` 写成规范的 **timestamp 扩展**（tag `0xFF`，32/64/96 三档） | §0-3（ISO 字符串 + `TryParse` 占满 DES 成本） | `d4da7cc` | `Metrics DES` **533 → 188 ns（−65%）**，载荷 53 → 30 B | `.superpowers/sdd/perf-mp-ts/report.md` §1–§2 |
| 8 | 修复 flaky ①：迭代数夹取逻辑抽成**纯函数**，测试改用合成成本 | 测试测量的是机器，不是逻辑 | `4fb9773` | 修复前全量 **1/26** 概率失败 | `.superpowers/sdd/flaky/report.md`、`fix.md` |
| 9 | 修复 flaky ②：预热超时测试改为断言**协议事实** | 断言吞吐量（预算里能跑几个 burst） | `854bcdd` | 4× 超订时原断言 **7/10 失败** | `.superpowers/sdd/flaky2/report.md`、`fix.md` |
| 10 | README 性能章节**全部重测** | 数字过时比空缺更危险 | `dc8e177` | 见 §7 | `README.md` Performance |
| 11 | MsgPack 读侧：span 模式走独立 tokenizer（`Read()` 分流 → `ReadSpan`，seq 原体逐字搬入 `ReadSequential`） | P1-1（读侧 `Read()` 每 token 9–11 ns） | `d45edca` + `d9334ed` | `DES MTenInts` **0.87×**、`DES MThirty` **0.91×**、`DES MMetrics` **0.87×**、tokenize **0.78–0.83×**（CONTROL 0.97–0.99× 证明收益来自新函数体）；**远低于归因预估的 2.33× → 1.5×** | `.superpowers/sdd/perf-mp-readspan/report.md` §5.2 |

## 3. 数字可追溯表

| README / 本文数字 | 来源 |
|---|---|
| `LargeFlat 50 DES` 6.120 → 3.095 µs | `perf-p01/report.md` §5.1 |
| `LargeFlat 50 SER` 2.244 → 1.874 µs | `perf-p02/report.md` §5.1 |
| `LargeString 1KB DES` 1.660 → 0.261 µs | `perf-p03/report.md` §0 |
| 宽对象写 1126 → 528 ns | `perf-mp-span/report.md` |
| 分配 600 / 1832 / 424 → 0 B/op | `perf-mp-str/report.md` §4 |
| `Metrics DES` 533 → 188 ns、53 → 30 B | `perf-mp-ts/report.md` §1 |
| 当前四格式对照表（JSON 1.43×、MessagePack 0.96×、TOML 0.19×、YAML 0.04×） | `README.md` Performance；原始日志在 `.superpowers/sdd/readme-perf/logs/`（30 个日志，三轮取中位） |

## 4. 被否决的修法及理由

| 曾考虑 | 为什么否决 |
|---|---|
| 把 flaky 断言 `>` 改成 `>=` | 会消失，但测试从此**发现不了"返回常数"** —— 它唯一存在的理由。改法是抽出纯函数，而不是弱化断言 |
| MsgPack 保留 ISO 字符串写侧 | 放弃 65% 的 DES 收益与全部互操作；且它会让 msgpack 成为库里**唯一不能与生态对话**的格式 |
| 为免分配走"栈编码"或"经 `Buffer` 交接" | 实测**收益为 0 或更慢**（`perf-mp-str/report.md` §6.2） |
| 给 `FlushToBuffer` 改 `api/` 基线 | 基线记录**上次发布的面**，手改等于伪造发版 diff；正解是改成 `internal`，生成代码经既有的 `Buffer` 出口交接 |
| 手工合并生成代码基线 | 基线应由**合并后的生成器重新生成**；手工拼会掩盖生成器的真实输出 |

## 5. 修掉的真实 bug（不是性能）

| bug | 现象 | 为什么差点漏掉 |
|---|---|---|
| `timestamp64` 字段序 | 规范载荷（秒 = 1）解成 **1992 年** | 自写自读**能往返**（两边用同一位序）⇒ 只有规范测试或互操作才能发现 |
| strict 测试的 `BuildMap` **按值复制 `ref struct`** | 载荷 6 字节被截断成 **2 字节**（`81 00`） | 两条测试只断言"抛 `FormatException`"，而截断的输入**同样**会抛 ⇒ 测试空转 |
| flaky ①（校准） | 全量测试 **1/26** 概率红 | 断言依赖真实计时，只在机器忙时失败 |
| flaky ②（预热超时） | 4× 超订时 **7/10** 红 | 断言的是"预算里能跑几个 burst"——吞吐量事实 |

## 6. 能力变化与取舍

**MsgPack 往返后 `DateTimeKind` 归一为 `Utc`。**

- **为什么**：timestamp 扩展的载荷只有「秒 + 纳秒」，即一个**瞬时**，规范里**没有 Kind 位**。
- **影响到什么**：`Unspecified` / `Local` 读回成 `Utc`；`Local` 是**转换**（`ToUniversalTime`），
  **tick 不变**，没有任何瞬间移动。
- **竞品同样如此**：MessagePack-CSharp 读同样字节省同样给 `Utc`（逐 tick 断言见
  `PicoMsgPack/tests/PicoMsgPack.Tests/TimestampInteropTests.cs`）。
- **代价的呈现方式**：能力探针 4 行 `OK → MISMATCH`（cell `S2`/`S4`，observation `S14`/`S15`），
  规格表 §3 写明原因，**没有**让基线偷偷同意。
- **替代方案**：保留 ISO 字符串 ⇒ 放弃 65% 收益与互操作（见 §4）。

**公共 API**：本轮登记了 4 条纯新增（`MsgPackWriter.WriteString(ReadOnlySpan<char>)`、
`WriteDateTime`、`MsgPackDateTimeFormat` 枚举、`MsgPackOptions.DateTimeFormat`），
全部记在 `api/PENDING-CHANGES.md`，`api/*.public.txt` **保持"上次发布的面"不动**。

## 7. 仍然落后的地方（诚实）

| 格式 | 当前状态（本轮重测） | 说明 |
|---|---|---|
| **JSON** | AOT **12 个场景慢 9 个**，比例 0.76×–3.56×，中位 **1.43×**；JIT 慢 10/12，中位 **1.53×** | 三条主因已消除，仍落后；继续拉近需要**新的归因**，本轮的归因已被用尽 |
| **MessagePack** | JIT 下**持平**（中位 **0.96×**，8 个场景 5 前 3 后）；AOT 2 前 2 后（中位 0.86×） | DES 与集合行仍落后 |
| **TOML** | **10/10 更快**，中位 **0.19×**（快 2.5–10 倍） | 对手 Tomlyn 2.10.1 typed + AOT，同等工作 |
| **YAML** | **6/6 更快**，中位 **0.04×**（快 17–50 倍） | 对手 YamlDotNet 18.1.0 仅 JIT，无 AOT 对手 |
| **INI** | **无比值** | 对手 ini-parser 无等价操作 ⇒ 只给绝对成本（`NON-EQUIVALENT`） |

测量条件（README 已写明）：单机、三轮取中位、AOT 与 JIT **各自独立进程**、共享桌面而非空闲实验机；
本轮有 **32** 个臂测量被负载判据拒绝，**9/30** 个进程在 `PICOBENCH_STRICT=1` 下以 `1` 退出。
每条发布的数字都建立在**三个未被拒的轮次**上，被拒的臂在聚合表里**点名**而不是悄悄平均掉。

## 8. 未做 / 已知欠账

| 项 | 状态 |
|---|---|
| Task 4e（MsgPack 读侧纯 span 快路径） | **已做**（`d45edca` + 防漂移补强 `d9334ed`）。**实测 0.87×–0.92×（DES 行，8–13%），而非归因预估的 2.33× → 1.5×**；CONTROL 0.97–0.99× 证明收益来自新函数体。**归因错在把状态机的成本记到了模式分支头上** —— 手写裸 span 解码 28 ns vs 读器 334 ns，要追平需换 token 化架构（P2 级重写） |
| 其它语言 README 同步 | **未同步**（本轮只改英文主文件） |
| 第三次 flaky | 40 次单跑**未复现**；仅在再现时再猎捕 |
| JSON 继续拉近 System.Text.Json | **未做**，需要新的归因 |
| 4d 的 `IsoString` 回落路径 | 已实现并有测试；但**未**在基准里对比其成本 |

## 9. 方法论沉淀

| 规矩 | 来自哪次教训 |
|---|---|
| 性能改动一律"**快路径 + 回退**"，回退路径**逐字保留** | JSON 三改、MsgPack 三改；P0-3 的转义路径一行未动 |
| "**零行为变化**"必须用**逐字节对照**证明，不能写在散文里 | P0-2 用三类命名策略 + 转义用例逐字节对照 |
| 快路径的护栏必须**独立于被测实现**（golden 来自旧实现） | 4b 的 12 条 golden hex 取自旧实现 |
| 测试只准断言**协议事实**，不准断言**吞吐量事实** | 两条 flaky 的共同病根 |
| 断言里出现**真实计时**，就要先问"它测的是代码还是这台机器" | flaky ① |
| `api/*.public.txt` 是"上次发布的面"，新增进**台账**而不是改基线 | 4b / 4c / 4d 连续三次撞同一面墙 |
| 生成代码基线必须由**生成器重新生成**，不手工合并 | 4d 与 4c 的合并冲突 |
| 数字**没有来源就不写**，没测到写"未测" | README 重测 |
| 重复代码只允许以"**被检查的副本**"形式存在 | 4e：同样 200 行状态机存在两份，代价靠三件事抵消 —— 派生脚本入仓、每次测试重新派生并断言一致、token 家族清单 |
| 护栏要**验证它抓得住**，不能只看它绿 | 4e：故意把 `ReadSpan` 内一个常量改错，确认守卫变红而构建仍绿 |

---

## 10. 补充：契约工程（fused token reader）与重测结果

### 10.1 为什么会有这一节

§1 的"六条主因"解决之后，README 里 JSON 仍有 9/12 行落后于 System.Text.Json。做归因时发现原因不是某个局部热点，
而是 **`ITokenReader` 的两段式契约**：`Read()` 先分类字节，`TryGetX()` 再解码一次 —— **同一个字节被 switch 两遍**。
对比的参照物是 MemoryPack（分析见 `.superpowers/sdd/memorypack-analysis/report.md`）：它快是因为**wire 里没有需要分类的东西**，
我们不可能取消 tag，但可以让 tag **只被 switch 一次**。

契约在 Core 里，影响五个格式，所以按 spec → plan → 逐任务执行（`docs/superpowers/specs/2026-10-28-fused-token-reader-design.md`、
`docs/superpowers/plans/2026-10-28-fused-token-reader.md`）。

### 10.2 机制在第一个任务就被证伪了两次

| 尝试 | 结果 |
|---|---|
| **DIM（默认接口实现）** | **30 个 CS9245**：`ref struct` 必须自己实现接口全部成员，即使有默认体 |
| **扩展方法** | 编译能过 —— **但那个形状是静默错**：`this T reader` 按值传，`Read()` 推进副本 ⇒ **死循环** |

最终机制：**派生接口 `IFusedTokenReader` + Core 静态参考正本 `TokenReaderFused`**（六个 `ref T` 方法，只存在一份）。

> **这一节最值得记的不是机制，而是顺序**：因为先写 spec，错误在**第一个任务**暴露，代价约等于零；
> 若直接开工，会在五个格式上各改一遍之后才发现。

### 10.3 交付与不交付（如实）

| 任务 | 结果 |
|---|---|
| 契约 + 参考正本 + 五格式实现 | ✅ |
| 两套"双走 dump"护栏（MsgPack 795,334 行 / JSON 1,339 行测试） | ✅ 均证明过"**注入故障会红**" |
| MsgPack 融合读（int32/int64/float/bool）+ 计数循环 | ✅ 合并，但**收益小**（int32 per-token 10%、其余噪声内） |
| **JSON 融合读 + 生成器发射** | ✅ **有真实收益**：`TenInts` **−30%/token** |
| **JSON 负数流式缺陷（F-1）** | ✅ **真实 bug**：`TryReadNextInt32Seq` 读负数错，已删除该变体，两路径统一 |
| T4（per-token 微基准闸门） | ❌ 不建 —— 计划引用的工具在仓库里不存在 |
| T16–T18（TOML） | ❌ 不做 —— TOML 已 **10/10 快于竞品**，条件不成立 |

**收益为什么比预估小**：归因说"二次反推占 22.3%"，但 `_intValue`/`_intKind` 缓存（来自 Collection 读优化）已经把其中大部分
提前拿走了。契约工程的**主要价值是结构**：五个格式共享一个可对拍、可负控、可逐格式优化的契约。

### 10.4 重测：JSON 与 MessagePack 两行的真实变化

测量窗口 **21:45–21:53**（`a4c0773`），协议不变（三轮取中位、AOT/JIT 分进程、`other_cpu < 35%`、被拒臂点名）。

| 行 | 08:41 那次 | 21:45 重测 |
|---|---|---|
| **JSON AOT** | 12 场景慢 **9** 个，0.76×–3.56×，中位 **1.43×** | 慢 **8** 个，**0.60×–1.46×**，中位 **1.15×** |
| **JSON JIT** | 慢 **10** 个，0.98×–4.72×，中位 **1.53×** | 慢 **7** 个，**0.67×–1.66×**，中位 **1.10×** |
| **MessagePack AOT** | 3–1，0.60×–1.13×，中位 **0.80×** | 3–1，**0.54×–1.04×**，中位 **0.70×** |
| **MessagePack JIT** | 4–4，0.48×–1.96×，中位 **0.96×** | 4–4，**0.57×–1.56×**，中位 **0.96×** |

**关键事实**：
- JSON 最差的一行从 **4.72×** 降到 **1.66×** —— 没有一行超过 1.66×。
- **6 个格现在领先超过 ±10% 带**：`Collection DES` **0.60×/0.67×**、`Collection SER` 0.80×、`Nested DES` 0.88×、`LargeString 1KB DES` **0.82×/0.84×**。
- 这些收益来自写侧三项（span 缓存、转义向量化、编译期属性名），不是来自融合读（融合读只帮整数密集行）。

### 10.5 这一轮新增的方法论

| 规矩 | 来自哪一次 |
|---|---|
| **先写会红的测试，看着它红**；没红过的测试不算数 | plan §3.5；T1/T2 是"先实现后补测试"，被记录下来而不是回填 |
| **性能覆写的红只能来自负控**（注入故障 → 护栏红 → 还原） | T6 的 5 种注入、T12 的 27 处注入 |
| **护栏必须验"咬得住"，不能只看它绿** | 三次：JSON 守卫（0x7F→0x7E）、msgpack 派生守卫、双走 dump |
| **`ref struct` 相关的"能编过"不等于正确** | DIM → 扩展方法（静默死循环）、`BuildMap` 按值复制（丢 4 字节） |
| **公共面新增进台账，不改基线** | 4b / 4c / 4d 连续三次撞墙后建立 `api/PENDING-CHANGES.md` |
| **多泳道并行时，构建会互相干扰** | 一次"1 个错误"实为并发构建，串行重跑即 0 |
