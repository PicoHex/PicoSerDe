# PicoSerDe TODO List

> 420 tests ✅ | 0 build errors | 2026-06-01 (updated)

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

## 🟡 P2 — v2 架构演进 (待规划)

以下 gap 全部需要 Reader/SG 层的 **多 token 发射** 或 **递归解析** 能力。
当前 Reader 的 "一次 Read() 返回一个 token" 模型不支持。

### TOML (1 项)
- [ ] **Dotted keys `a.b.c = value`** — Reader 需 token 缓冲队列，将点号分隔键拆为多个 ObjectStart + 最终 PropertyName。SG 已可处理 `[a.b]` 表头，仅 Reader 变更。

### YAML (3 项)
- [ ] **Block scalar 完整实现** — 当前仅读取原始缩进块。需：base indent stripping、`|+`/`|-` chomping、`>` folded 模式（换行→空格）
- [ ] **Tag 序列化 `!type`** — Reader 已跳过 tag 标记；Writer 需 `WriteTag()` API；SG 需 `[YamlTag]` attribute 或 converter 生成 `!type` 前缀
- [ ] **嵌套序列 `List<List<T>>`** — Writer `WriteSequenceItem` 无嵌套；SG 深层反序列化缺失

### MsgPack (1 项)
- [ ] **Ext 类型 SG 生成** — Reader 已解析 `Extension` token (tag+data)；SG 缺 `WriteSer`/`WriteDeser` 的 `extension` 分支。需 TypeKindResolver 返回 `"extension"` 类型映射

### JSON (1 项)
- [ ] **`[JsonConstructor]` 参数化构造** — SG 反序列化器需：检测构造函数 → 参数名→属性名映射 → temp 变量 → 构造函数调用。记录类型和不可变类必需

### 全局 (1 项)
- [ ] **嵌套集合 `List<List<T>>` — 所有 5 个 SG** — `ExtractNestedProperties` 仅解析一层 list/dict 元素类型。需递归解析 + 各 SG 代码生成深层嵌套反序列化循环

---

## 🟢 P3 — 全部完成 ✅
