# 计划：REC-01 / REC-05 / POLY-01 / POLY-02 彻底修复（TDD）

分支：`fix/streaming-root-cause`（延续）。基线：`122143a`，1477 tests / 0 failed，构建 0 warning。
约束：每个修复先制造红灯（构建红或断言红），再实现；共享层优先；0 warning 门禁；不改 `main`。

## REC-01 — `List<List<TObject>>` 生成不可编译代码（P1，构建阻断）

- 现象：CS0234（缺 `...JsonInner`）+ CS1503（`List<object>` → `List<T>` 不匹配）；非递归同样失败（父提交同样失败，已复现）。
- 红灯：`tests/PicoSerDe.Integration.Tests/NestedListOfObjectsTests.cs`
  - `List<List<PlainObj>>` sync round-trip（非递归）
  - `List<List<Node>>` 递归 round-trip + streaming
- 预期修复点（待定位后确认）：
  - 提取：`BuildNestedListElement` 链末端为 object kind 时需提取其属性（注册 helper），并设置环目标（`ElementTypeFullName`）/`IsRecursiveRef`。
  - 发射：内层列表声明类型需用元素类型（而非 `object`）；反序列化内层列表创建正确类型。
- 验收：以上测试绿；JSON/MsgPack（支持对象列表的格式）一致；TOML/YAML/INI 若不支持需明确诊断/忽略而非不可编译。

## REC-05 — 可空元素类型（P1）

- 现象：`List<int?>`/`List<string?>` → CS1503/CS0029/CS0019/CS8619（JSON+MsgPack）；`List<TObject?>` → JSON CS8604（缺 null 检查，运行时经 `SerializeCustom`/inner helper）、MsgPack CS8619。
- 红灯：`tests/PicoSerDe.Integration.Tests/NullableElementTests.cs`
  - `List<int?>` / `List<string?>` / `List<TObject?>` / `Dictionary<string,TObject?>`：编译 + round-trip（含 null 元素）
- 预期修复点：
  - 声明侧使用 `ElementTypeNameAnnotated`（保持 `?`）生成 `List<T?>`/`Dictionary<K,V?>`。
  - 序列化侧对可空元素发 null 检查（`WriteNull`）。
  - 反序列化侧可空值类型元素走 `int?` 解析路径；引用类型元素允许 null。
- 验收：JSON/MsgPack 全绿；TOML/YAML/INI 对其支持的形态一致（不支持的形态保持"响亮失败/文档化忽略"）。

## POLY-01 — poly 派生类型复杂成员被丢弃（P1，MsgPack/TOML/YAML/INI）

- 现象：poly 派生 ser/de 过滤 `IsComplexMember`（对象成员），嵌套对象静默丢失（JSON 正常）。
- 红灯：`PolyInheritanceTests` 恢复 `Address.City` 断言（MsgPack）；TOML/YAML 加同等断言（若格式支持嵌套对象）。
- 修复点：poly 派生链对复杂成员发射与普通路径一致的嵌套读取（MsgPack）；TOML/YAML 需评估嵌套对象在 poly 分支的可表达性，INI 保持忽略并文档化。

## POLY-02 — 递归 × 多态（P1）

- 现象：环目标为多态类型时，播种内层 helper 生成 `new T()`（抽象基类 CS0144）且不按 discriminator 路由；嵌套派生值退化为基类型。
- 红灯：恢复 `PolyNode`（抽象基类 + 递归成员）fixture：
  - 抽象基类 → `...JsonInner`/`...MsgPackInner` 编译失败（CS0144）
  - 具体基类 → 嵌套 `Next` 反序列化后 `IsTypeOf<PolyLeaf>()` 失败
- 修复点：多态感知的内层 helper：在既有 reader 上读 discriminator → 内联派生类型成员读取（JSON/MsgPack 各自实现；流式续传语义需保持）。
- 验收：抽象与具体基类两种 fixture 全绿；既有 poly 测试不回归。

## 收尾

- 每批：全量 9 工程测试 + `-t:Rebuild` 0 warning，CSharpier，独立提交。
- 最后：更新 `docs/bug-audit-report-2026-08-16.md` 状态，更新 README/skill（如行为矩阵变化），执行 finishing-a-development-branch 流程提交合并决策。

## 执行状态（2026-09-18 暂停点）

- ✅ REC-01：`5242089`（JSON/MsgPack 真支持；TOML/YAML/INI PICOSERDE004 诊断式丢弃；MsgPack 原来写 `ToString()`/读 `default!` 的静默损坏一并修复）。
- ✅ REC-05：`98ab504`（`Nullable<T>` 注解保留；JSON/MsgPack 元素 null 读写；TOML/YAML 保留"跳过 null 标量"语义；INI 丢弃一切可空元素；YAML 空分发链守卫）。
- 🔶 POLY-01：MsgPack 已修（poly 派生 ser 全成员 + de 用 inner helper 读对象成员）；TOML/YAML 剩余 —— 需把 poly 派生 de 循环改成根循环形状（ObjectStart/TablePath 段分发 + ArrayStart）并让 TOML poly ser 用 `EmitSerializeProp`；INI 按文档保持忽略。
- ⬜ POLY-02（递归 × 多态）：未开始 —— 需"多态感知内层 helper"（在既有 reader 上读 discriminator 后内联/路由派生类型；JSON 与 MsgPack 各自实现，含流式续传）。

## 完成（2026-09-18）

- ✅ REC-01 `5242089`、REC-05 `98ab504`、POLY-01 `343199c`+`e329d63`、POLY-02（本提交）。
- POLY-02 实现：`PolyClassPrefix`（JSON）/`MsgPackPolyPrefix`（MsgPack）跨文件类名（`internal`）；poly ser/de 增加 `DeserializeCore(ref reader, options)`；inner helper 改为 `GenPolyInner` 路由；poly core 采用"当前 token=对象起始"约定 + chunk 守卫；JSON poly 流式 per-member 快照 + `NeedMoreData` 回退并在派生分支 `reader.NeedsMoreData → RewindTo(__snap)`。
- 附带：MsgPack 普通递归对象成员走 helper（原写 null/读 default!）。
- 验证：9 工程 1497 tests / 0 failed；Rebuild 0 warning / 0 error。

## POLY-01 集合成员补修（2026-09-18）

- poly 派生类型的集合成员（`List<TObject>` / `Dictionary<string,TObject>`）此前生成不可编译代码或丢值；三格式 poly 分支改为复用共享成员发射器（TOML: `EmitSerializeProp`/`EmitPropertyDispatch`/`EmitNestedObjectRead`/`EmitDictRead` + 新增 ArrayStart 分支；YAML: `EmitSerialize`/`EmitDeserialize` + 普通路径循环形状；MsgPack: `WriteDeser`）。
- 顺带修复 `dti.CtorParams` default ImmutableArray 的 `.Length` NRE（曾使 TOML/YAML 生成器整体 CS8785）。
- 验证：`PolyCollectionMemberTests` 4/4；全量 1501 tests / 0 failed；Rebuild 0 warning / 0 error。

## POLY-03 补修（2026-09-18）

- 具体多态基类实例此前输出畸形 `{"$type":}`（JSON）/`$type = ""`（TOML）。
- 共享 `BaseDiscriminator(TypeInfo)`（`TypeInfo.IsAbstract` 新增字段）合成基类 discriminator；5 个格式 poly ser/de 把基类作为最后一个 case → 基类实例可往返。
- 测试 `PolyConcreteBaseTests` 6/6；全量 1507 tests / 0 failed。

## Code review 批次复审（2026-09-18）

- RV-01（REC-05 嵌套可空元素）✅ 修；RV-02（三层嵌套列表变量遮蔽）✅ 修；回归 `EdgeCaseRegressionTests` 8/8。
- RV-03（字段级对象数组 `TObject[]`：TOML/YAML 不可编译；`List<TObject[]>` JSON 不可编译）❌ 预存在（基线 `ecaa69d` worktree 复现）。
- RV-04（`Dictionary<string, List<T>>` 不可编译）❌ 预存在（基线复现）。
