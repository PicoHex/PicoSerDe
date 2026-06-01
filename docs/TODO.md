# PicoSerDe TODO List

> 441 tests ✅ | 0 build errors | 2026-06-01 (all v2 tasks complete)

---

## ✅ 本次完成 (2026-06-01 TDD)

---

### Phase 12: JSON [JsonConstructor]
- [x] **SG 构造函数检测** — `DetectJsonConstructor` 辅助 + `CtorParamInfo`
- [x] **Deserializer 构造调用** — temp 变量 → `new T(p0, p1, ...)`
- [x] **readonly 属性** — `includeReadOnlyProperties` 参数支持 getter-only 属性

### Phase 13: TOML Dotted Keys
- [x] **Reader 点号分割** — inline offset buffer (4 parts) + `EmitDottedTokenSpan`
- [x] **TablePath 兼容** — `BuildDottedPathUpTo` 复用现有 SG 路径
- [x] **旧测试更新** — `DottedKey_ParsesAsSingleKey` → `DottedKey_EmitsObjectStartSequence`

### Phase 14: YAML Block Scalar 增强
- [x] **Chomping `|+`/`|-`** — 保留/剥离尾随换行
- [x] **Folded `>` 模式** — 单换行→空格
- [x] **Indent stripping** — 逐行剥离 baseIndent + 缓冲区写入

### Phase 15: YAML Tag 基础设施
- [x] **`YamlTagAttribute`** — `[AttributeUsage(Class | Struct)]`
- [x] **`YamlWriter.WriteTag()`** — 写入 `!tag ` 前缀

---

## ✅ 已完成 (2026-05-31 ~ 2026-06-01)

### Phase 8: 审计修复 (3 P1 bugs)
- [x] **INI list 逗号数据损坏** — TDD: +4 tests; backslash-escape 替代裸 comma-join/split
- [x] **TOML SG dict 编译错误** — TDD: +4 tests; EmitDictRead 补全 int64/float64/bool/datetime 等类型分支
- [x] **YAML SG inner list/dict 缺失** — TDD: +2 tests; EmitSerializeInline/DeserializeInline 补 list/array/dict + ExtractNested dict 元数据

### Phase 9: SG 架构统一
- [x] **5 个 SG 统一 TransformType** — INI/TOML/YAML/MsgPack 删除独立 ~200行 Transform，统一到 `GenInfrastructure.TransformType`
- [x] **PropertyInfo 扩展** — 新增 `IntKey`, `SectionName`, `Comment` 可选字段
- [x] **AttributeHelpers 扩展** — 新增 `GetIntKey`, `GetSectionName`, `GetComment`, `GetPropertyComment`, `OverrideKindWithStringOnConverter`
- [x] **TOML `__T.__Tk` 冗余** — 替换为共享 `TextHelpers.Eq`

### Phase 10: 性能优化
- [x] **SIMD Vector128→256→512** — `SimdHelpers` + `ContainsBackslash` 三层分层 SIMD
- [x] **JSON SG `stackalloc`** — `new T[256]` → `stackalloc T[256]` (scoped Span 签名)
- [x] **Reader 8-field 零分配** — JSON/TOML/YAML Reader 统一为 INI 首创的内联 8-field buffer 追踪
- [x] **MsgPack `_singleByte`** — `new byte[1]` → `MemoryMarshal.CreateSpan(ref _singleByte, 1)`

### Phase 11: 格式特性补全
- [x] **INI Dict 支持** — 序列化为 Section，反序列化动态键累加
- [x] **INI Comment 输出** — 类级 + 属性级 `[IniComment]` 序列化
- [x] **INI `Enum.TryParse`** — 无效枚举值不再抛异常
- [x] **JSON 严格前导零** — `01` / `-01` 抛 FormatException (RFC 8259 §6)
- [x] **TOML 特殊浮点** — `+nan`/`inf`/`+inf`/`-inf` 字面量解析
- [x] **TOML Inline table Writer** — `WriteStartInlineTable` / `WriteEndInlineTable`
- [x] **YAML anchor 零分配** — Dictionary/List 替换为 4 组内联字段 (max 4 anchors, 8 pairs)
- [x] **YAML block scalar `|`** — 最小实现：检测 `|` `>` 并读取缩进块

---

## 🟡 P2 — v2 架构演进 ✅ 全部完成

所有 v2 任务已通过 TDD 完成实现。

### TOML (1 项)
- [x] **Dotted keys `a.b.c = value`** — ✅ Reader inline offset buffer + EmitDottedTokenSpan

### YAML (3 项)
- [x] **Block scalar 完整实现** — ✅ Chomping `+`/`-`、folded `>`、indent stripping
- [x] **Tag 序列化 `!type`** — ✅ `YamlTagAttribute` + `WriteTag()` + SG 自动注入 `!tag`
- [x] **嵌套序列 `List<List<T>>`** — ✅ YAML SG 嵌套序列化（JSON SG 序列化+反序列化）

### MsgPack (1 项)
- [x] **Ext 类型 SG 生成** — ✅ `MsgPackExtensionTagAttribute` + WriteExtension/ReadExtension + TypeKindResolver

### JSON (1 项)
- [x] **`[JsonConstructor]` 参数化构造** — ✅ 构造函数检测 → temp 变量 → 构造调用

### 全局 (1 项)
- [x] **嵌套集合 `List<List<T>>`** — ✅ `GenInfrastructure.TransformType` 嵌套 list 检测 + JSON SG 代码生成

---

## 🟢 P3 — 全部完成 ✅

无待办项。
