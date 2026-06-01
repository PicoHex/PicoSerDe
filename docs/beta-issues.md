# Beta 模块问题清单

> 441 tests ✅ | 6 modules | 2026-06-01 (updated, all v2 tasks complete)

---

## ✅ 本次修复 (2026-06-01 TDD session)

---

### JSON [JsonConstructor] — 新增
- [x] **SG: 构造函数检测 + temp 变量 + 构造调用** — `DetectJsonConstructor` 辅助函数 + `EmitDeserializeCtorParam`
- [x] **readonly 属性支持** — `includeReadOnlyProperties` 参数

### TOML Dotted Keys — 新增
- [x] **Reader: 点号分割键** — inline offset buffer + `EmitDottedTokenSpan`
- [x] **TablePath 构建** — `BuildDottedPathUpTo` 兼容现有 SG

### YAML Block Scalar 增强
- [x] **Chomping `+`/`-`** — 保留/剥离尾随换行
- [x] **Folded `>`** — 单换行→空格
- [x] **Indent stripping** — 逐行剥离 baseIndent

### YAML Tag 基础
- [x] **`YamlTagAttribute`** — 创建属性定义
- [x] **`YamlWriter.WriteTag()`** — API 就绪

---

## ✅ 已修复 (2026-05-31 ~ 2026-06-01)

### MsgPack (4/4 已修复)
- [x] **Reader: 构造器堆分配** — `new int[64]`/`new bool[64]` → `[InlineArray(64)]` struct
- [x] **SG: `static _varCounter` 改局部** — static→`ref int` 参数传递
- [x] **SG: bytes 支持** — TypeKindResolver + SG WriteBytes/ReadBytes
- [x] **SG: 支持 ext 扩展类型** — ✅ TDD session 完成 (MsgPackExtensionTagAttribute + WriteExtension)

### TOML (3/3 已修复)
- [x] **Reader: `goto StartSpan` 重构** — goto→结构化 `while(true)+flag`
- [x] **全局: Trim/TrimEnd 去重** — 统一到 `TextHelpers`
- [x] **全局: IsDigit 去重** — 统一到 `TextHelpers`

### YAML (4/4 已修复)
- [x] **Reader: 多文档 `---`** — 已支持
- [x] **Reader: 复杂键 `?`** — 已支持
- [x] **Reader: `!type` / `&anchor` 跳过** — Sequence 模式已添加
- [x] **Reader: 锚点堆分配** — Dictionary/List → 4 组内联字段 (max 4 anchors × 8 pairs)

### 新增修复 (审计驱动)
- [x] **INI: list 逗号损坏** — backslash-escape 替代裸 join/split
- [x] **TOML: SG dict 编译错误** — EmitDictRead 全类型分支
- [x] **YAML: SG inner list/dict 缺失** — EmitSerializeInline/DeserializeInline 补全
- [x] **5 SG Transform 统一** — 删除 4 份 ~200 行重复 Transform
- [x] **SIMD Vector128→256→512** — 三层分层
- [x] **JSON `stackalloc`** — `new T[256]` → `stackalloc T[256]`
- [x] **Reader 构造器零分配** — 统一 8-field 内联 buffer 追踪
- [x] **JSON 严格前导零** — RFC 8259 §6
- [x] **TOML 特殊浮点** — +nan/inf 字面量
- [x] **INI Dict + Comment + Enum.TryParse**
- [x] **YAML block scalar** — 最小实现

---

## 🟡 v2 规划 — 需要架构变更

> 全部需要 Reader/SG 层的多 token 发射或递归解析能力

| 模块 | 项目 | 状态 | 说明 |
|------|------|------|------|
| JSON | `[JsonConstructor]` | ✅ | SG 构造函数检测 + temp 变量 + 构造调用 |
| TOML | Dotted keys | ✅ | Reader inline offset buffer + EmitDottedTokenSpan |
| YAML | Block scalar | ✅ | Chomping +/-、folded >、indent stripping |
| YAML | Tag 序列化 `!type` | ✅ | YamlTagAttribute + WriteTag() + SG 自动注入 |
| 全局 | `List<List<T>>` | ✅ | JSON SG (序列化+反序列化) + YAML SG (序列化) |
| MsgPack | ext SG 生成 | ✅ | MsgPackExtensionTagAttribute + WriteExtension/ReadExtension |

所有 v2 任务完成 🎉

---

## 按优先级排序

```
v1.0 完成线 (已达成):
  Bug 修复 → SG 统一 → 性能优化 → 特性补全

v2.0 规划线 (建议顺序):
  P1: JSON JsonConstructor → TOML dotted keys → YAML block scalar 完整
  P2: YAML Tag → 嵌套集合
  P3: MsgPack ext SG
```
