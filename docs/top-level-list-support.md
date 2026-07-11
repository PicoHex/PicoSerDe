# `List<SomeDto>` Implementation Plan — Verified

## 1. YAML (~15 lines)

**Ser**: `EmitSerializeListElement` 加 `"object"` case（已在属性级代码 line 1585 验证）:
```csharp
case "object":
    var sn = InnerClassName("YamlInner", p.ElementTypeName!);
    s.Append(ind); s.AppendLine("yw.WriteStartSequenceBlock();");
    s.Append(ind); s.Append(sn); s.AppendLine(".SerializeBlock(yw, __item);");
    s.Append(ind); s.AppendLine("yw.WriteEndSequenceBlock();");
    break;
```

**Deser**: `EmitDeserializeListElementTemp` 加 `"object"` case（已在属性级代码 line 2073 验证）:
```csharp
case "object":
    var sn = InnerClassName("YamlInner", p.ElementTypeName!);
    s.Append(pad); s.Append("__tmpList.Add("); s.Append(sn);
    s.AppendLine(".Deserialize(ref r));");
    break;
```

**GenerateAll**: 加 `ArrayElementNestedProps` 收集循环。

**GenList**: 不改。现有 `while(r.Read()){if(String) EmitDeserializeListElementTemp(...)}` 循环中，String token（空 `- ` 标记）后 `Deserialize` 读到 `ObjectStart`→属性→`ObjectEnd`。

---

## 2. TOML (~25 lines)

**Ser**: `EmitSerializeListElement` 加 `"object"`:
```csharp
case "object":
    var sn = InnerClassName("TomlInner", p.ElementTypeName!);
    s.Append(ind); s.Append(sn); s.AppendLine(".Serialize(tw, __item);");
    break;
```

**Deser**: 不用 `EmitDeserializeListElementTemp`（它的 `Deserialize` 以 `ObjectEnd` 终止，array-of-tables 用 `ArrayStart` 分隔，不兼容）。改用**内联属性分发**（已在属性级代码 line 1733 验证）:

```csharp
// 不在 EmitDeserializeListElementTemp 里加 case，而是在 GenList 里分支：
if (ek == "object" && elemP.NestedProperties.Length > 0)
{
    // 内联: while(r.Read()){if(ArrayStart)break; ...}
    s.AppendLine("while (r.Read()) {");
    s.AppendLine("    if (r.TokenType != TokenType.ArrayStart) break;");
    s.AppendLine("    var __item = new SomeDto();");
    s.AppendLine("    while (r.Read() && r.TokenType == TokenType.PropertyName) {");
    s.AppendLine("        var __k = r.KeySpan;");
    // 属性分发（key 匹配 Name, Age...）→ 调用 EmitPropertyDispatch
    s.AppendLine("    }");
    s.AppendLine("    __tmpList.Add(__item);");
    s.AppendLine("}");
}
```

属性分发代码 TOML 已有——`EmitPropertyDispatch`。GenList 直接复用。

**GenList 序列化**也需分支：基本类型用 `WriteStartArray/WriteEndArray`，对象类型用 `WriteArrayTable` per element。

**GenerateAll**: 已在 v2026.3.20 加入 ✅

---

## 3. MsgPack (~120 lines)

**GenInner**（新建，参考 `Gen` line 306）:
```csharp
internal static class SomeDtoMsgPackInner {
    internal static void Serialize(ref MsgPackWriter mw, SomeDto v) {
        mw.WriteStartObject(N);
        for each p: mw.WriteInt32(p.IntKey); WriteSer(p, "v." + p.Name);
        mw.WriteEndObject();
    }
    internal static SomeDto Deserialize(ref MsgPackReader reader) {
        var obj = new SomeDto();
        // int-key dispatch loop (same pattern as Gen)
        return obj;
    }
}
```

和 `Gen` 的区别：输出 `static class` 而非 `file readonly struct`；参数名 `v` 而非 `value`；传给 `WriteSer` 的 accessor 是 `v.Name` 而非 `value.Name`。

**GenerateAll**: 加 `CollectNestedTypes` + `ArrayElementNestedProps` 收集 + inner helper 生成循环。

**WriteSerElem**: 加 `"object"` → `sn.Serialize(ref mw, __item)`

**ReadDeserElem**: 加 `"object"` → `sn.Deserialize(ref reader)`

**GenList**: 不改。

---

## 4. INI (~200 lines)

**Ser**（已有模式 line 469 验证）: section 化输出
```csharp
// GenList serializer for objects:
for (int i = 0; i < v.Count; i++) {
    iw.WriteSection(i.ToString()u8);
    foreach (var np in nestedProps) {
        iw.WriteKeyValue(np.JsonNameu8, WriteValue(np, v[i].np.Name));
    }
}
```

**Deser**: 需要手动解析 section 边界。INI reader 按 `key=value` 行解析。读到 `[N]` section 头时完成前一个对象。

```csharp
// GenList deserializer:
SomeDto? current = null;
while (reader.ReadLine()) {
    if (line starts with '[') {
        if (current != null) __list.Add(current);
        current = new SomeDto();
    } else if (current != null && line contains '=') {
        // parse key=value → assign to current property
    }
}
if (current != null) __list.Add(current);
```

**GenerateAll**: 完整 inner helper 基础设施（`CollectNestedTypes` + 收集 + `GenInner` 生成循环）。

**WriteValue/EmitReadValue**: 加 `"object"` case（虽然 GenList 走 section 路径不需要，但为了完整性）。

**GenList**: 基本类型和对象类型走完全不同的序列化/反序列化路径。基本类型用逗号分隔字符串；对象类型用索引化 section。

---

## 总结

| | YAML | TOML | MsgPack | INI |
|---|---|---|---|---|
| GenerateAll 收集 | +3 | ✅ 已做 | +15 | +15 |
| 元素 ser helper | +8 | +5 | +5 | — |
| 元素 deser helper | +6 | —(内联) | +5 | — |
| GenInner | ✅ 已有 | ✅ 已有 | +90 新建 | +100 新建 |
| GenList 分支 | 0 | +15 | 0 | +70 |
| 测试 | 1 | 1 | 1 | 1 |

YAML 最简单——加 case 就行。TOML 需要 GenList 分支和内联 deser。MsgPack 需要 GenInner。INI 全都要——GenInner + section 化 GenList。
