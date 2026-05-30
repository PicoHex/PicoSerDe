namespace PicoJson.Gen;

[Generator(LanguageNames.CSharp)]
public sealed class JsonSerializerGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var typeProviders = context
            .SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => IsCandidate(node),
                transform: static (ctx, _) => Transform(ctx)
            )
            .Where(static t => t is not null)
            .Select(static (t, _) => t!.Value);

        context.RegisterSourceOutput(
            typeProviders.Collect(),
            static (spc, types) => GenerateAll(spc, types)
        );
    }

    // ── Candidate detection ──

    private static bool IsCandidate(SyntaxNode node)
    {
        if (node is not InvocationExpressionSyntax { Expression: var expr })
            return false;

        SimpleNameSyntax? name = expr switch
        {
            MemberAccessExpressionSyntax { Name: var n } => n,
            MemberBindingExpressionSyntax { Name: var n } => n,
            _ => null,
        };

        string? methodName = name switch
        {
            GenericNameSyntax gn => gn.Identifier.Text,
            SimpleNameSyntax sn => sn.Identifier.Text,
            _ => null,
        };

        return methodName is "Serialize" or "SerializeToUtf8Bytes" or "Deserialize";
    }

    private static TypeInfo? Transform(GeneratorSyntaxContext ctx)
    {
        if (ctx.SemanticModel.GetSymbolInfo(ctx.Node).Symbol is not IMethodSymbol method)
            return null;

        if (
            method.ContainingType.Name != "JsonSerializer"
            || method.ContainingType.ContainingNamespace?.ToDisplayString() != "PicoJson"
        )
            return null;

        if (method.TypeArguments.Length != 1)
            return null;

        var typeArg = method.TypeArguments[0];
        if (typeArg.SpecialType != SpecialType.None)
            return null;
        if (typeArg is not INamedTypeSymbol namedType)
            return null;
        if (namedType.ContainingType is not null)
            return null;

        var ns = namedType.ContainingNamespace?.ToDisplayString() ?? "";
        if (ns == "<global namespace>")
            ns = "";
        var useCamelCase = HasJsonCamelCase(namedType);
        var properties = new List<PropertyInfo>();

        foreach (var member in namedType.GetMembers())
        {
            if (member is not IPropertySymbol prop)
                continue;
            if (prop.DeclaredAccessibility != Accessibility.Public)
                continue;
            if (prop.IsStatic || prop.IsIndexer)
                continue;
            if (prop.IsReadOnly && prop.SetMethod is null)
                continue;
            if (prop.GetMethod is null)
                continue;
            if (HasJsonIgnore(prop))
                continue;

            var (typeKind, isNullable, innerTypeSymbol) = PicoSerDe
                .Gen
                .TypeKindResolver
                .Resolve(prop.Type);
            if (typeKind is null)
                continue;

            string? elementTypeKind = null;
            string? elementTypeName = null;
            string? keyTypeKind = null;
            string? keyTypeName = null;
            ImmutableArray<PropertyInfo> nestedProperties = ImmutableArray<PropertyInfo>.Empty;

            if (typeKind is "list" or "array")
            {
                ITypeSymbol? elementType;
                if (prop.Type is IArrayTypeSymbol arrType)
                    elementType = arrType.ElementType;
                else if (prop.Type is INamedTypeSymbol ntsElem && ntsElem.TypeArguments.Length == 1)
                    elementType = ntsElem.TypeArguments[0];
                else
                    continue;

                var (ek, _, _) = PicoSerDe.Gen.TypeKindResolver.Resolve(elementType);
                if (ek is null)
                    continue;
                elementTypeKind = ek;
                elementTypeName = PicoSerDe.Gen.TypeKindResolver.MapTypeName(ek, elementType);
                if (ek is "object" && elementType is INamedTypeSymbol eNtsObj)
                    nestedProperties = ExtractNestedProperties(eNtsObj, useCamelCase);
            }
            else if (typeKind is "dict")
            {
                if (prop.Type is INamedTypeSymbol ntsDict && ntsDict.TypeArguments.Length == 2)
                {
                    var keyType = ntsDict.TypeArguments[0];
                    var valType = ntsDict.TypeArguments[1];
                    var (kk, _, _) = PicoSerDe.Gen.TypeKindResolver.Resolve(keyType);
                    var (vk, _, _) = PicoSerDe.Gen.TypeKindResolver.Resolve(valType);
                    if (kk is null || vk is null)
                        continue;
                    keyTypeKind = kk;
                    keyTypeName = PicoSerDe.Gen.TypeKindResolver.MapTypeName(kk, keyType);
                    elementTypeKind = vk;
                    elementTypeName = PicoSerDe.Gen.TypeKindResolver.MapTypeName(vk, valType);
                    if (vk is "object" && valType is INamedTypeSymbol vNtsObj)
                        nestedProperties = ExtractNestedProperties(vNtsObj);
                }
                else
                    continue;
            }
            else if (typeKind is "object" && prop.Type is INamedTypeSymbol objNts)
            {
                nestedProperties = ExtractNestedProperties(objNts);
            }

            properties.Add(
                new PropertyInfo(
                    prop.Name,
                    GetJsonPropertyName(prop)
                        ?? (useCamelCase ? ToCamelCase(prop.Name) : prop.Name),
                    typeKind,
                    prop.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    isNullable,
                    elementTypeKind,
                    elementTypeName,
                    keyTypeKind,
                    keyTypeName,
                    nestedProperties,
                    GetJsonConverterType(prop),
                    GetDateTimeFormat(prop)
                )
            );
        }

        return new TypeInfo(
            namedType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            ns,
            namedType.Name,
            properties.ToImmutableArray()
        );
    }

    // ── Nested property extraction ──

    private static ImmutableArray<PropertyInfo> ExtractNestedProperties(
        INamedTypeSymbol type,
        bool useCamelCase = false
    )
    {
        var list = new List<PropertyInfo>();
        foreach (var member in type.GetMembers())
        {
            if (member is not IPropertySymbol prop)
                continue;
            if (prop.DeclaredAccessibility != Accessibility.Public)
                continue;
            if (prop.IsStatic || prop.IsIndexer)
                continue;
            if (prop.IsReadOnly && prop.SetMethod is null)
                continue;
            if (prop.GetMethod is null)
                continue;
            if (HasJsonIgnore(prop))
                continue;

            var (typeKind, isNullable, _) = PicoSerDe.Gen.TypeKindResolver.Resolve(prop.Type);
            if (typeKind is null)
                continue;

            string? elementTypeKind = null;
            string? elementTypeName = null;
            string? keyTypeKind = null;
            string? keyTypeName = null;
            ImmutableArray<PropertyInfo> nestedProperties = ImmutableArray<PropertyInfo>.Empty;

            if (typeKind is "list" or "array")
            {
                ITypeSymbol? elementType;
                if (prop.Type is IArrayTypeSymbol arrType)
                    elementType = arrType.ElementType;
                else if (prop.Type is INamedTypeSymbol ntsElem && ntsElem.TypeArguments.Length == 1)
                    elementType = ntsElem.TypeArguments[0];
                else
                    continue;

                var (ek, _, _) = PicoSerDe.Gen.TypeKindResolver.Resolve(elementType);
                if (ek is null)
                    continue;
                elementTypeKind = ek;
                elementTypeName = PicoSerDe.Gen.TypeKindResolver.MapTypeName(ek, elementType);
                if (ek is "object" && elementType is INamedTypeSymbol eNtsObj)
                    nestedProperties = ExtractNestedProperties(eNtsObj, useCamelCase);
            }
            else if (typeKind is "dict")
            {
                if (prop.Type is INamedTypeSymbol ntsDict && ntsDict.TypeArguments.Length == 2)
                {
                    var keyType = ntsDict.TypeArguments[0];
                    var valType = ntsDict.TypeArguments[1];
                    var (kk, _, _) = PicoSerDe.Gen.TypeKindResolver.Resolve(keyType);
                    var (vk, _, _) = PicoSerDe.Gen.TypeKindResolver.Resolve(valType);
                    if (kk is null || vk is null)
                        continue;
                    keyTypeKind = kk;
                    keyTypeName = PicoSerDe.Gen.TypeKindResolver.MapTypeName(kk, keyType);
                    elementTypeKind = vk;
                    elementTypeName = PicoSerDe.Gen.TypeKindResolver.MapTypeName(vk, valType);
                    if (vk is "object" && valType is INamedTypeSymbol vNtsObj)
                        nestedProperties = ExtractNestedProperties(vNtsObj);
                }
                else
                    continue;
            }
            else if (typeKind is "object" && prop.Type is INamedTypeSymbol objNts)
            {
                nestedProperties = ExtractNestedProperties(objNts);
            }

            list.Add(
                new PropertyInfo(
                    prop.Name,
                    GetJsonPropertyName(prop)
                        ?? (useCamelCase ? ToCamelCase(prop.Name) : prop.Name),
                    typeKind,
                    prop.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    isNullable,
                    elementTypeKind,
                    elementTypeName,
                    keyTypeKind,
                    keyTypeName,
                    nestedProperties,
                    GetJsonConverterType(prop),
                    GetDateTimeFormat(prop)
                )
            );
        }
        return list.ToImmutableArray();
    }

    // ── Attribute helpers ──

    private static string? GetJsonPropertyName(IPropertySymbol prop)
    {
        foreach (var attr in prop.GetAttributes())
        {
            if (
                attr.AttributeClass?.Name == "JsonPropertyNameAttribute"
                && attr.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoJson"
                && attr.ConstructorArguments.Length == 1
                && attr.ConstructorArguments[0].Value is string name
            )
                return name;
        }
        return null;
    }

    private static bool HasJsonIgnore(IPropertySymbol prop)
    {
        foreach (var attr in prop.GetAttributes())
        {
            if (
                attr.AttributeClass?.Name == "JsonIgnoreAttribute"
                && attr.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoJson"
            )
                return true;
        }
        return false;
    }

    private static string? GetJsonConverterType(IPropertySymbol prop)
    {
        foreach (var attr in prop.GetAttributes())
        {
            if (
                attr.AttributeClass?.Name == "JsonConverterAttribute"
                && attr.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoJson"
                && attr.ConstructorArguments.Length == 1
                && attr.ConstructorArguments[0].Value is INamedTypeSymbol converterType
            )
                return converterType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }
        return null;
    }

    private static string? GetDateTimeFormat(IPropertySymbol prop)
    {
        foreach (var attr in prop.GetAttributes())
        {
            if (
                attr.AttributeClass?.Name == "DateTimeFormatAttribute"
                && attr.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoJson"
                && attr.ConstructorArguments.Length == 1
                && attr.ConstructorArguments[0].Value is string format
            )
                return format;
        }
        return null;
    }

    private static bool HasJsonCamelCase(INamedTypeSymbol type)
    {
        foreach (var attr in type.GetAttributes())
        {
            if (
                attr.AttributeClass?.Name == "JsonCamelCaseAttribute"
                && attr.AttributeClass.ContainingNamespace?.ToDisplayString() == "PicoJson"
            )
                return true;
        }
        return false;
    }

    private static string ToCamelCase(string name)
    {
        if (name.Length == 0)
            return name;
        // Handle consecutive uppercase (e.g., "XMLParser" → "xmlParser")
        int upperCount = 0;
        while (upperCount < name.Length && char.IsUpper(name[upperCount]))
            upperCount++;
        if (upperCount <= 1)
            return char.ToLowerInvariant(name[0]) + name.Substring(1);
        // Convert all but last uppercase to lowercase
        int keepUpper = upperCount > 1 && upperCount < name.Length ? upperCount - 1 : upperCount;
        return name.Substring(0, keepUpper).ToLowerInvariant() + name.Substring(keepUpper);
    }

    // ── Source generation ──

    private static void GenerateAll(SourceProductionContext spc, ImmutableArray<TypeInfo> types)
    {
        var seen = new HashSet<string>();

        // Collect all unique nested object types (M×N dedup: emit once, reference from parents)
        var nestedTypes = new Dictionary<string, ImmutableArray<PropertyInfo>>();
        foreach (var type in types)
            CollectNestedTypes(type, nestedTypes);

        // Generate inner helpers for shared nested types
        foreach (var kv in nestedTypes)
        {
            var fullName = kv.Key;
            var props = kv.Value;
            var cleanName = fullName.Replace("global::", "");
            var shortName = ShortName(cleanName);
            spc.AddSource(
                $"{shortName}_JsonInner.g.cs",
                SourceText.From(GenerateInnerHelper(cleanName, shortName, props), Encoding.UTF8)
            );
        }

        // Generate main type files
        foreach (var type in types)
        {
            if (!seen.Add(type.FullyQualifiedName))
                continue;
            var source = GenerateTypeCode(type);
            spc.AddSource(
                $"{type.Name}_JsonSerializer.g.cs",
                SourceText.From(source, Encoding.UTF8)
            );
        }
    }

    private static void CollectNestedTypes(
        TypeInfo type,
        Dictionary<string, ImmutableArray<PropertyInfo>> nestedTypes
    )
    {
        foreach (var prop in type.Properties)
        {
            if (prop.TypeKind == "object" && !string.IsNullOrEmpty(prop.TypeFullName))
                AddNestedType(prop.TypeFullName, prop.NestedProperties, nestedTypes);
            if (
                (prop.TypeKind == "list" || prop.TypeKind == "array")
                && prop.ElementTypeKind == "object"
                && !string.IsNullOrEmpty(prop.ElementTypeName)
            )
                AddNestedType(prop.ElementTypeName!, prop.NestedProperties, nestedTypes);
            if (
                prop.TypeKind == "dict"
                && prop.ElementTypeKind == "object"
                && !string.IsNullOrEmpty(prop.ElementTypeName)
            )
                AddNestedType(prop.ElementTypeName!, prop.NestedProperties, nestedTypes);
        }
    }

    private static void AddNestedType(
        string fullName,
        ImmutableArray<PropertyInfo> props,
        Dictionary<string, ImmutableArray<PropertyInfo>> nestedTypes
    )
    {
        if (nestedTypes.ContainsKey(fullName))
            return;
        nestedTypes[fullName] = props;

        // Recursively collect nested types from this nested type's properties
        foreach (var np in props)
        {
            if (np.TypeKind == "object" && !string.IsNullOrEmpty(np.TypeFullName))
                AddNestedType(np.TypeFullName, np.NestedProperties, nestedTypes);
            if (
                (np.TypeKind == "list" || np.TypeKind == "array")
                && np.ElementTypeKind == "object"
                && !string.IsNullOrEmpty(np.ElementTypeName)
            )
                AddNestedType(np.ElementTypeName!, np.NestedProperties, nestedTypes);
            if (
                np.TypeKind == "dict"
                && np.ElementTypeKind == "object"
                && !string.IsNullOrEmpty(np.ElementTypeName)
            )
                AddNestedType(np.ElementTypeName!, np.NestedProperties, nestedTypes);
        }
    }

    private static string ShortName(string fullName)
    {
        var name = fullName.Replace("global::", "");
        var lastDot = name.LastIndexOf('.');
        return lastDot >= 0 ? name.Substring(lastDot + 1) : name;
    }

    private static string GenerateInnerHelper(
        string fullName,
        string shortName,
        ImmutableArray<PropertyInfo> props
    )
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Buffers;");
        sb.AppendLine("using System.Text;");
        sb.AppendLine("using PicoSerDe.Core;");
        sb.AppendLine("using PicoJson;");

        // Extract namespace from fullName
        var lastDot = fullName.LastIndexOf('.');
        if (lastDot > 0)
        {
            var ns = fullName.Substring(0, lastDot);
            sb.AppendLine();
            sb.Append("namespace ");
            sb.Append(ns);
            sb.AppendLine(";");
        }
        sb.AppendLine();
        sb.Append("internal static class ");
        sb.Append(shortName);
        sb.AppendLine("JsonInner");
        sb.AppendLine("{");

        // Serialize helper
        sb.Append("    internal static void Serialize(JsonWriter jw, ");
        sb.Append(fullName);
        sb.AppendLine(" value)");
        sb.AppendLine("    {");
        sb.AppendLine("        jw.WriteStartObject();");
        foreach (var prop in props)
        {
            sb.Append("        jw.WritePropertyName(\"");
            sb.Append(prop.JsonName);
            sb.AppendLine("\"u8);");
            EmitSerializeProperty(sb, prop, "value." + prop.Name, "        ");
        }
        sb.AppendLine("        jw.WriteEndObject();");
        sb.AppendLine("    }");
        sb.AppendLine();

        // Deserialize helper
        sb.Append("    internal static ");
        sb.Append(fullName);
        sb.AppendLine(" Deserialize(ref JsonReader reader)");
        sb.AppendLine("    {");
        sb.Append("        var obj = new ");
        sb.Append(fullName);
        sb.AppendLine("();");
        sb.AppendLine(
            "        while (reader.Read() && reader.TokenType == TokenType.PropertyName)"
        );
        sb.AppendLine("        {");
        sb.AppendLine("            var __n = reader.GetStringRaw();");
        sb.AppendLine("            reader.Read();");
        for (int i = 0; i < props.Length; i++)
        {
            var np = props[i];
            var kw = i == 0 ? "if" : "else if";
            sb.Append("            ");
            sb.Append(kw);
            sb.Append(" (TextHelpers.Eq(__n, \"");
            sb.Append(np.JsonName);
            sb.AppendLine("\"u8))");
            sb.AppendLine("            {");
            EmitDeserializeProperty(sb, np, "obj", "                ");
            sb.AppendLine("            }");
        }
        if (props.Length > 0)
            sb.AppendLine("            else reader.TrySkip();");
        else
            sb.AppendLine("            reader.TrySkip();");
        sb.AppendLine("        }");
        sb.AppendLine("        return obj;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string GenerateTypeCode(TypeInfo type)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Buffers;");
        sb.AppendLine("using System.Text;");
        sb.AppendLine("using PicoSerDe.Core;");
        sb.AppendLine("using PicoJson;");

        if (!string.IsNullOrEmpty(type.Namespace))
        {
            sb.AppendLine();
            sb.Append("namespace ");
            sb.Append(type.Namespace);
            sb.AppendLine(";");
        }

        sb.AppendLine();
        EmitSerializer(sb, type);
        sb.AppendLine();
        EmitDeserializer(sb, type);
        sb.AppendLine();
        EmitRegistration(sb, type);
        return sb.ToString();
    }

    // ── Serializer emission ──

    private static void EmitSerializer(StringBuilder sb, TypeInfo type)
    {
        sb.Append("    file readonly struct ");
        sb.Append(type.Name);
        sb.Append("JsonSerializer : ISerializer<");
        sb.Append(type.Name);
        sb.AppendLine(">");
        sb.AppendLine("    {");
        sb.Append("        public void Serialize(IBufferWriter<byte> writer, ");
        sb.Append(type.Name);
        sb.AppendLine(" value)");
        sb.AppendLine("        {");
        sb.AppendLine("            var jw = new JsonWriter(writer);");
        sb.AppendLine("            jw.WriteStartObject();");

        foreach (var prop in type.Properties)
        {
            sb.Append("            jw.WritePropertyName(\"");
            sb.Append(prop.JsonName);
            sb.AppendLine("\"u8);");
            EmitSerializeProperty(sb, prop, $"value.{prop.Name}", "            ");
        }

        sb.AppendLine("            jw.WriteEndObject();");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
    }

    private static void EmitSerializeProperty(
        StringBuilder sb,
        PropertyInfo prop,
        string accessor,
        string indent
    )
    {
        // Custom converter
        if (prop.ConverterTypeFullName is not null)
        {
            sb.Append(indent);
            sb.Append("var __conv = new ");
            sb.Append(prop.ConverterTypeFullName);
            sb.AppendLine("();");
            sb.Append(indent);
            sb.Append("__conv.Write(writer, ");
            sb.Append(accessor);
            sb.AppendLine(");");
            return;
        }

        // Nullable wrapping
        var effectiveAccessor = accessor;
        if (prop.IsNullable)
        {
            sb.Append(indent);
            sb.Append("if (");
            sb.Append(accessor);
            sb.AppendLine(".HasValue)");
            sb.Append(indent);
            sb.AppendLine("{");
            effectiveAccessor = $"{accessor}.Value";
            indent += "    ";
        }

        switch (prop.TypeKind)
        {
            case "string":
                sb.Append(indent);
                sb.Append("jw.WriteString(Encoding.UTF8.GetBytes(");
                sb.Append(effectiveAccessor);
                sb.AppendLine("));");
                break;
            case "int32":
            case "int64":
            case "float64":
                sb.Append(indent);
                sb.Append("jw.WriteNumber(");
                sb.Append(effectiveAccessor);
                sb.AppendLine(");");
                break;
            case "boolean":
                sb.Append(indent);
                sb.Append("jw.WriteBoolean(");
                sb.Append(effectiveAccessor);
                sb.AppendLine(");");
                break;
            case "datetime":
                sb.Append(indent);
                sb.Append("var __iso_");
                sb.Append(prop.Name);
                sb.Append(" = ");
                sb.Append(effectiveAccessor);
                if (prop.DateTimeFormat is not null)
                {
                    sb.Append(".ToString(\"");
                    sb.Append(prop.DateTimeFormat);
                    sb.AppendLine("\");");
                }
                else
                    sb.AppendLine(".ToString(\"O\");");
                sb.Append(indent);
                sb.Append("jw.WriteString(Encoding.UTF8.GetBytes(__iso_");
                sb.Append(prop.Name);
                sb.AppendLine("));");
                break;
            case "dateonly":
                sb.Append(indent);
                sb.Append("var __d = ");
                sb.Append(effectiveAccessor);
                sb.AppendLine(".ToString(\"O\");");
                sb.Append(indent);
                sb.AppendLine("jw.WriteString(Encoding.UTF8.GetBytes(__d));");
                break;
            case "timeonly":
                sb.Append(indent);
                sb.Append("var __t = ");
                sb.Append(effectiveAccessor);
                sb.AppendLine(".ToString(\"O\");");
                sb.Append(indent);
                sb.AppendLine("jw.WriteString(Encoding.UTF8.GetBytes(__t));");
                break;
            case "timespan":
                sb.Append(indent);
                sb.Append("var __ts = ");
                sb.Append(effectiveAccessor);
                sb.AppendLine(".ToString();");
                sb.Append(indent);
                sb.AppendLine("jw.WriteString(Encoding.UTF8.GetBytes(__ts));");
                break;
            case "guid":
            case "enum":
                sb.Append(indent);
                sb.Append("jw.WriteString(Encoding.UTF8.GetBytes(");
                sb.Append(effectiveAccessor);
                sb.AppendLine(".ToString()));");
                break;
            case "decimal":
                sb.Append(indent);
                sb.Append("jw.WriteString(Encoding.UTF8.GetBytes(");
                sb.Append(effectiveAccessor);
                sb.AppendLine(".ToString(System.Globalization.CultureInfo.InvariantCulture)));");
                break;
            case "list":
            case "array":
                sb.Append(indent);
                sb.AppendLine("jw.WriteStartArray();");
                sb.Append(indent);
                sb.Append("foreach (var __item in ");
                sb.Append(effectiveAccessor);
                sb.AppendLine(")");
                sb.Append(indent);
                sb.AppendLine("{");
                EmitSerializeElement(sb, prop, "__item", indent + "    ");
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.AppendLine("jw.WriteEndArray();");
                break;
            case "dict":
                sb.Append(indent);
                sb.AppendLine("jw.WriteStartObject();");
                sb.Append(indent);
                sb.Append("foreach (var __kvp in ");
                sb.Append(effectiveAccessor);
                sb.AppendLine(")");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.Append("    jw.WritePropertyName(Encoding.UTF8.GetBytes(__kvp.Key");
                if (prop.KeyTypeKind == "string")
                    sb.AppendLine("));");
                else
                    sb.AppendLine(".ToString()));");
                EmitSerializeElement(sb, prop, "__kvp.Value", indent + "    ");
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.AppendLine("jw.WriteEndObject();");
                break;
            case "object":
            {
                var sn = ShortName(prop.TypeFullName!);
                sb.Append(indent);
                sb.Append("if (");
                sb.Append(effectiveAccessor);
                sb.AppendLine(" == null) jw.WriteNull();");
                sb.Append(indent);
                sb.AppendLine("else");
                sb.Append(indent);
                sb.Append("    ");
                sb.Append(sn);
                sb.Append("JsonInner.Serialize(jw, ");
                sb.Append(effectiveAccessor);
                sb.AppendLine(");");
                break;
            }
        }

        // Close nullable block
        if (prop.IsNullable)
        {
            indent = indent.Substring(0, indent.Length - 4);
            sb.Append(indent);
            sb.AppendLine("}");
            sb.Append(indent);
            sb.AppendLine("else");
            sb.Append(indent);
            sb.AppendLine("    jw.WriteNull();");
        }
    }

    private static void EmitSerializeElement(
        StringBuilder sb,
        PropertyInfo prop,
        string itemVar,
        string indent
    )
    {
        switch (prop.ElementTypeKind!)
        {
            case "string":
                sb.Append(indent);
                sb.Append("jw.WriteString(Encoding.UTF8.GetBytes(");
                sb.Append(itemVar);
                sb.AppendLine("));");
                break;
            case "int32":
            case "int64":
            case "float64":
                sb.Append(indent);
                sb.Append("jw.WriteNumber(");
                sb.Append(itemVar);
                sb.AppendLine(");");
                break;
            case "boolean":
                sb.Append(indent);
                sb.Append("jw.WriteBoolean(");
                sb.Append(itemVar);
                sb.AppendLine(");");
                break;
            case "datetime":
                sb.Append(indent);
                sb.Append("jw.WriteString(Encoding.UTF8.GetBytes(");
                sb.Append(itemVar);
                sb.AppendLine(".ToString(\"O\")));");
                break;
            case "dateonly":
                sb.Append(indent);
                sb.Append("jw.WriteString(Encoding.UTF8.GetBytes(");
                sb.Append(itemVar);
                sb.AppendLine(".ToString(\"O\")));");
                break;
            case "timeonly":
                sb.Append(indent);
                sb.Append("jw.WriteString(Encoding.UTF8.GetBytes(");
                sb.Append(itemVar);
                sb.AppendLine(".ToString(\"O\")));");
                break;
            case "timespan":
                sb.Append(indent);
                sb.Append("jw.WriteString(Encoding.UTF8.GetBytes(");
                sb.Append(itemVar);
                sb.AppendLine(".ToString()));");
                break;
            case "guid":
            case "enum":
                sb.Append(indent);
                sb.Append("jw.WriteString(Encoding.UTF8.GetBytes(");
                sb.Append(itemVar);
                sb.AppendLine(".ToString()));");
                break;
            case "decimal":
                sb.Append(indent);
                sb.Append("jw.WriteString(Encoding.UTF8.GetBytes(");
                sb.Append(itemVar);
                sb.AppendLine(".ToString(System.Globalization.CultureInfo.InvariantCulture)));");
                break;
            case "object":
            {
                var sn = ShortName(prop.ElementTypeName!);
                sb.Append(indent);
                sb.Append(sn);
                sb.Append("JsonInner.Serialize(jw, ");
                sb.Append(itemVar);
                sb.AppendLine(");");
                break;
            }
            default:
                sb.Append(indent);
                sb.Append("jw.WriteString(Encoding.UTF8.GetBytes(");
                sb.Append(itemVar);
                sb.AppendLine(".ToString()));");
                break;
        }
    }

    // ── Deserializer emission ──

    private static void EmitDeserializer(StringBuilder sb, TypeInfo type)
    {
        sb.Append("    file readonly struct ");
        sb.Append(type.Name);
        sb.Append("JsonDeserializer : IDeserializer<");
        sb.Append(type.Name);
        sb.AppendLine(">");
        sb.AppendLine("    {");
        sb.Append("        public ");
        sb.Append(type.Name);
        sb.AppendLine(" Deserialize(ReadOnlySpan<byte> data)");
        sb.AppendLine("        {");
        sb.AppendLine("            var reader = new JsonReader(data);");
        sb.Append("            var obj = new ");
        sb.Append(type.Name);
        sb.AppendLine("();");
        sb.AppendLine("            reader.Read();");
        sb.AppendLine(
            "            while (reader.Read() && reader.TokenType == TokenType.PropertyName)"
        );
        sb.AppendLine("            {");
        sb.AppendLine("                var propNameSpan = reader.GetStringRaw();");
        sb.AppendLine("                reader.Read();");
        sb.AppendLine();

        for (var i = 0; i < type.Properties.Length; i++)
        {
            var prop = type.Properties[i];
            var keyword = i == 0 ? "if" : "else if";
            sb.Append("                ");
            sb.Append(keyword);
            sb.Append(" (TextHelpers.Eq(propNameSpan, \"");
            sb.Append(prop.JsonName);
            sb.AppendLine("\"u8))");
            sb.AppendLine("                {");
            EmitDeserializeProperty(sb, prop, "obj", "                    ");
            sb.AppendLine("                }");
        }

        if (type.Properties.Length > 0)
            sb.AppendLine("                else reader.TrySkip();");
        else
            sb.AppendLine("                reader.TrySkip();");

        sb.AppendLine("            }");
        sb.AppendLine("            return obj;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
    }

    private static void EmitDeserializeProperty(
        StringBuilder sb,
        PropertyInfo prop,
        string target,
        string indent,
        int nestLevel = 0
    )
    {
        // Custom converter
        if (prop.ConverterTypeFullName is not null)
        {
            sb.Append(indent);
            sb.Append("var __conv = new ");
            sb.Append(prop.ConverterTypeFullName);
            sb.AppendLine("();");
            sb.Append(indent);
            sb.Append(target);
            sb.Append(".");
            sb.Append(prop.Name);
            sb.AppendLine(" = __conv.Read(ref reader);");
            return;
        }

        // Nullable
        if (prop.IsNullable && prop.TypeKind != "object")
        {
            sb.Append(indent);
            sb.AppendLine("if (reader.TokenType == TokenType.Null)");
            sb.Append(indent);
            sb.Append("    ");
            sb.Append(target);
            sb.Append(".");
            sb.Append(prop.Name);
            sb.AppendLine(" = null;");
            sb.Append(indent);
            sb.AppendLine("else");
            sb.Append(indent);
            sb.AppendLine("{");
            EmitDeserializeValue(sb, prop, target, indent + "    ", nestLevel);
            sb.Append(indent);
            sb.AppendLine("}");
            return;
        }

        EmitDeserializeValue(sb, prop, target, indent, nestLevel);
    }

    private static void EmitDeserializeValue(
        StringBuilder sb,
        PropertyInfo prop,
        string target,
        string indent,
        int nestLevel
    )
    {
        switch (prop.TypeKind)
        {
            case "string":
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = Encoding.UTF8.GetString(reader.GetStringRaw());");
                break;
            case "int32":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetInt32(out var __intValue);");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __intValue;");
                break;
            case "int64":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetInt64(out var __longValue);");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __longValue;");
                break;
            case "float64":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetFloat64(out var __doubleValue);");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __doubleValue;");
                break;
            case "boolean":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetBool(out var __boolValue);");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __boolValue;");
                break;
            case "datetime":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                if (prop.DateTimeFormat is not null)
                {
                    sb.Append("System.DateTime.TryParseExact(__strValue, \"");
                    sb.Append(prop.DateTimeFormat);
                    sb.Append(
                        "\", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var __dt_"
                    );
                    sb.Append(prop.Name);
                    sb.AppendLine(");");
                }
                else
                {
                    sb.Append(
                        "System.DateTime.TryParse(__strValue, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var __dt_"
                    );
                    sb.Append(prop.Name);
                    sb.AppendLine(");");
                }
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.Append(" = __dt_");
                sb.Append(prop.Name);
                sb.AppendLine(";");
                break;
            case "guid":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("System.Guid.TryParse(__rawBytes, out var __guidValue);");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __guidValue;");
                break;
            case "decimal":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine(
                    "decimal.TryParse(__rawBytes, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var __decimalValue);"
                );
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __decimalValue;");
                break;
            case "dateonly":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.AppendLine("System.DateOnly.TryParse(__strValue, out var __dateOnlyValue);");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __dateOnlyValue;");
                break;
            case "timeonly":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.AppendLine("System.TimeOnly.TryParse(__strValue, out var __timeOnlyValue);");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __timeOnlyValue;");
                break;
            case "timespan":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.AppendLine("System.TimeSpan.TryParse(__strValue, out var __timeSpanValue);");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __timeSpanValue;");
                break;
            case "enum":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = System.Text.Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.Append("System.Enum.TryParse<");
                sb.Append(prop.TypeFullName);
                sb.AppendLine(">(__strValue, out var __enumValue);");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __enumValue;");
                break;
            case "list":
            case "array":
                if (prop.TypeKind == "list")
                {
                    sb.Append(indent);
                    sb.Append(target);
                    sb.Append(".");
                    sb.Append(prop.Name);
                    sb.AppendLine(" ??= new System.Collections.Generic.List<");
                    sb.Append(prop.ElementTypeName);
                    sb.AppendLine(">(16);");
                }
                else
                {
                    sb.Append(indent);
                    sb.Append("var __list_");
                    sb.Append(prop.Name);
                    sb.Append(" = new System.Collections.Generic.List<");
                    sb.Append(prop.ElementTypeName);
                    sb.AppendLine(">();");
                }
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.ArrayStart)");
                sb.Append(indent);
                sb.AppendLine("{");

                var listAcc =
                    prop.TypeKind == "list" ? $"{target}.{prop.Name}" : $"__list_{prop.Name}";

                if (prop.ElementTypeKind == "int32")
                {
                    // Inline loop: directly access raw buffer, skip all Reader overhead
                    sb.Append(indent);
                    sb.AppendLine("    var __d = reader.RawBuffer;");
                    sb.Append(indent);
                    sb.AppendLine("    var __p = reader.RawPos;");
                    sb.Append(indent);
                    sb.AppendLine("    var __e = __d.Length;");
                    sb.Append(indent);
                    sb.AppendLine("    while (__p < __e)");
                    sb.Append(indent);
                    sb.AppendLine("    {");
                    sb.Append(indent);
                    sb.AppendLine("        byte __b = __d[__p];");
                    sb.Append(indent);
                    sb.AppendLine("        if (__b <= 32) { __p++; continue; }");
                    sb.Append(indent);
                    sb.AppendLine("        if (__b == (byte)']') { __p++; break; }");
                    sb.Append(indent);
                    sb.AppendLine("        if (__b == (byte)',') { __p++; continue; }");
                    sb.Append(indent);
                    sb.AppendLine("        bool __neg = false;");
                    sb.Append(indent);
                    sb.AppendLine(
                        "        if (__b == (byte)'-') { __neg = true; __p++; __b = __d[__p]; }"
                    );
                    sb.Append(indent);
                    sb.AppendLine(
                        "        if (__b < (byte)'0' || __b > (byte)'9') { __p++; continue; }"
                    );
                    sb.Append(indent);
                    sb.AppendLine("        int __v = 0;");
                    sb.Append(indent);
                    sb.AppendLine(
                        "        do { __v = __v * 10 + (__b - (byte)'0'); __p++; if (__p >= __e) break; __b = __d[__p]; } while (__b >= (byte)'0' && __b <= (byte)'9');"
                    );
                    sb.Append(indent);
                    sb.AppendLine("        if (__neg) __v = -__v;");
                    sb.Append(indent);
                    sb.Append("        ");
                    sb.Append(listAcc);
                    sb.AppendLine(".Add(__v);");
                    sb.Append(indent);
                    sb.AppendLine("    }");
                    sb.Append(indent);
                    sb.AppendLine("    reader.SetRawPos(__p);");
                }
                else
                {
                    sb.Append(indent);
                    sb.AppendLine(
                        "    while (reader.Read() && reader.TokenType != TokenType.ArrayEnd)"
                    );
                    sb.Append(indent);
                    sb.AppendLine("    {");
                    EmitDeserializeElementAdd(sb, prop, listAcc, indent + "        ", nestLevel);
                    sb.Append(indent);
                    sb.AppendLine("    }");
                }

                sb.Append(indent);
                sb.AppendLine("}");
                if (prop.TypeKind == "array")
                {
                    sb.Append(indent);
                    sb.Append(target);
                    sb.Append(".");
                    sb.Append(prop.Name);
                    sb.Append(" = __list_");
                    sb.Append(prop.Name);
                    sb.AppendLine(".ToArray();");
                }
                break;
            case "dict":
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" ??= new System.Collections.Generic.Dictionary<");
                sb.Append(prop.KeyTypeName);
                sb.Append(", ");
                sb.Append(prop.ElementTypeName);
                sb.AppendLine(">();");
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.ObjectStart)");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.AppendLine(
                    "    while (reader.Read() && reader.TokenType == TokenType.PropertyName)"
                );
                sb.Append(indent);
                sb.AppendLine("    {");
                var dictAcc = $"{target}.{prop.Name}";
                EmitDeserializeDictKey(sb, prop, dictAcc, indent + "        ");
                sb.Append(indent);
                sb.AppendLine("        reader.Read();");
                EmitDeserializeElementAssign(
                    sb,
                    prop,
                    dictAcc,
                    "__dictKey",
                    indent + "        ",
                    nestLevel
                );
                sb.Append(indent);
                sb.AppendLine("    }");
                sb.Append(indent);
                sb.AppendLine("}");
                break;
            case "object":
            {
                var sn = ShortName(prop.TypeFullName!);
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.Null)");
                sb.Append(indent);
                sb.Append("    ");
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = null;");
                sb.Append(indent);
                sb.AppendLine("else");
                sb.Append(indent);
                sb.Append("    ");
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.Append(" = ");
                sb.Append(sn);
                sb.AppendLine("JsonInner.Deserialize(ref reader);");
                break;
            }
        }
    }

    private static void EmitNestedDeserialize(
        StringBuilder sb,
        ImmutableArray<PropertyInfo> props,
        string target,
        string indent,
        int nestLevel = 0,
        string propVarName = "__nestedPropName"
    )
    {
        for (var i = 0; i < props.Length; i++)
        {
            var np = props[i];
            var keyword = i == 0 ? "if" : "else if";
            sb.Append(indent);
            sb.Append(keyword);
            sb.Append(" (TextHelpers.Eq(");
            sb.Append(propVarName);
            sb.Append(", \"");
            sb.Append(np.JsonName);
            sb.AppendLine("\"u8))");
            sb.Append(indent);
            sb.AppendLine("{");
            EmitDeserializeProperty(sb, np, target, indent + "    ", nestLevel);
            sb.Append(indent);
            sb.AppendLine("}");
        }
    }

    private static void EmitDeserializeElementAdd(
        StringBuilder sb,
        PropertyInfo prop,
        string listVar,
        string indent,
        int nestLevel
    )
    {
        switch (prop.ElementTypeKind!)
        {
            case "string":
                sb.Append(indent);
                sb.Append(listVar);
                sb.AppendLine(".Add(Encoding.UTF8.GetString(reader.GetStringRaw()));");
                break;
            case "int32":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetInt32(out var __elementValue);");
                sb.Append(indent);
                sb.Append(listVar);
                sb.AppendLine(".Add(__elementValue);");
                break;
            case "int64":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetInt64(out var __elementValue);");
                sb.Append(indent);
                sb.Append(listVar);
                sb.AppendLine(".Add(__elementValue);");
                break;
            case "float64":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetFloat64(out var __elementValue);");
                sb.Append(indent);
                sb.Append(listVar);
                sb.AppendLine(".Add(__elementValue);");
                break;
            case "boolean":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetBool(out var __elementValue);");
                sb.Append(indent);
                sb.Append(listVar);
                sb.AppendLine(".Add(__elementValue);");
                break;
            case "datetime":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.AppendLine(
                    "System.DateTime.TryParse(__strValue, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var __dateTimeValue);"
                );
                sb.Append(indent);
                sb.Append(listVar);
                sb.AppendLine(".Add(__dateTimeValue);");
                break;
            case "guid":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("System.Guid.TryParse(__rawBytes, out var __guidValue);");
                sb.Append(indent);
                sb.Append(listVar);
                sb.AppendLine(".Add(__guidValue);");
                break;
            case "decimal":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine(
                    "decimal.TryParse(__rawBytes, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var __decimalValue);"
                );
                sb.Append(indent);
                sb.Append(listVar);
                sb.AppendLine(".Add(__decimalValue);");
                break;
            case "dateonly":
            case "timeonly":
            case "timespan":
                EmitDeserializeElementAddTemporal(sb, prop.ElementTypeKind, listVar, indent);
                break;
            case "enum":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = System.Text.Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.Append("System.Enum.TryParse<");
                sb.Append(prop.ElementTypeName);
                sb.AppendLine(">(__strValue, out var __enumValue);");
                sb.Append(indent);
                sb.Append(listVar);
                sb.AppendLine(".Add(__enumValue);");
                break;
            case "object":
            {
                var sn = ShortName(prop.ElementTypeName!);
                sb.Append(indent);
                sb.Append(listVar);
                sb.Append(".Add(");
                sb.Append(sn);
                sb.AppendLine("JsonInner.Deserialize(ref reader));");
                break;
            }
            default:
                sb.Append(indent);
                sb.Append(listVar);
                sb.AppendLine(".Add(Encoding.UTF8.GetString(reader.GetStringRaw()));");
                break;
        }
    }

    private static void EmitDeserializeElementAddTemporal(
        StringBuilder sb,
        string? kind,
        string listVar,
        string indent
    )
    {
        if (kind is null)
            return;
        sb.Append(indent);
        sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
        sb.Append(indent);
        sb.AppendLine("var __strValue = Encoding.UTF8.GetString(__rawBytes);");
        switch (kind)
        {
            case "dateonly":
                sb.Append(indent);
                sb.AppendLine("System.DateOnly.TryParse(__strValue, out var __dateOnlyValue);");
                sb.Append(indent);
                sb.Append(listVar);
                sb.AppendLine(".Add(__dateOnlyValue);");
                break;
            case "timeonly":
                sb.Append(indent);
                sb.AppendLine("System.TimeOnly.TryParse(__strValue, out var __timeOnlyValue);");
                sb.Append(indent);
                sb.Append(listVar);
                sb.AppendLine(".Add(__timeOnlyValue);");
                break;
            case "timespan":
                sb.Append(indent);
                sb.AppendLine("System.TimeSpan.TryParse(__strValue, out var __timeSpanValue);");
                sb.Append(indent);
                sb.Append(listVar);
                sb.AppendLine(".Add(__timeSpanValue);");
                break;
        }
    }

    private static void EmitDeserializeDictKey(
        StringBuilder sb,
        PropertyInfo prop,
        string dictVar,
        string indent
    )
    {
        switch (prop.KeyTypeKind!)
        {
            case "string":
                sb.Append(indent);
                sb.Append(
                    "var __dictKey = System.Text.Encoding.UTF8.GetString(reader.GetStringRaw());"
                );
                sb.AppendLine();
                break;
            case "int32":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("int.TryParse(__rawBytes, out var __dictKey);");
                break;
            case "int64":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("long.TryParse(__rawBytes, out var __dictKey);");
                break;
            case "guid":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("System.Guid.TryParse(__rawBytes, out var __dictKey);");
                break;
            case "enum":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = System.Text.Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.Append("System.Enum.TryParse<");
                sb.Append(prop.KeyTypeName);
                sb.AppendLine(">(__strValue, out var __dictKey);");
                break;
            default:
                sb.Append(indent);
                sb.Append(
                    "var __dictKey = System.Text.Encoding.UTF8.GetString(reader.GetStringRaw());"
                );
                sb.AppendLine();
                break;
        }
    }

    private static void EmitDeserializeElementAssign(
        StringBuilder sb,
        PropertyInfo prop,
        string dictVar,
        string keyVar,
        string indent,
        int nestLevel
    )
    {
        switch (prop.ElementTypeKind!)
        {
            case "string":
                sb.Append(indent);
                sb.Append(dictVar);
                sb.Append("[");
                sb.Append(keyVar);
                sb.AppendLine("] = Encoding.UTF8.GetString(reader.GetStringRaw());");
                break;
            case "int32":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetInt32(out var __elementValue);");
                sb.Append(indent);
                sb.Append(dictVar);
                sb.Append("[");
                sb.Append(keyVar);
                sb.AppendLine("] = __elementValue;");
                break;
            case "int64":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetInt64(out var __elementValue);");
                sb.Append(indent);
                sb.Append(dictVar);
                sb.Append("[");
                sb.Append(keyVar);
                sb.AppendLine("] = __elementValue;");
                break;
            case "float64":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetFloat64(out var __elementValue);");
                sb.Append(indent);
                sb.Append(dictVar);
                sb.Append("[");
                sb.Append(keyVar);
                sb.AppendLine("] = __elementValue;");
                break;
            case "boolean":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetBool(out var __elementValue);");
                sb.Append(indent);
                sb.Append(dictVar);
                sb.Append("[");
                sb.Append(keyVar);
                sb.AppendLine("] = __elementValue;");
                break;
            case "datetime":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.AppendLine(
                    "System.DateTime.TryParse(__strValue, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var __dateTimeValue);"
                );
                sb.Append(indent);
                sb.Append(dictVar);
                sb.Append("[");
                sb.Append(keyVar);
                sb.AppendLine("] = __dateTimeValue;");
                break;
            case "guid":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("System.Guid.TryParse(__rawBytes, out var __guidValue);");
                sb.Append(indent);
                sb.Append(dictVar);
                sb.Append("[");
                sb.Append(keyVar);
                sb.AppendLine("] = __guidValue;");
                break;
            case "decimal":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine(
                    "decimal.TryParse(__rawBytes, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var __decimalValue);"
                );
                sb.Append(indent);
                sb.Append(dictVar);
                sb.Append("[");
                sb.Append(keyVar);
                sb.AppendLine("] = __decimalValue;");
                break;
            case "dateonly":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.AppendLine("System.DateOnly.TryParse(__strValue, out var __dateOnlyValue);");
                sb.Append(indent);
                sb.Append(dictVar);
                sb.Append("[");
                sb.Append(keyVar);
                sb.AppendLine("] = __dateOnlyValue;");
                break;
            case "timeonly":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.AppendLine("System.TimeOnly.TryParse(__strValue, out var __timeOnlyValue);");
                sb.Append(indent);
                sb.Append(dictVar);
                sb.Append("[");
                sb.Append(keyVar);
                sb.AppendLine("] = __timeOnlyValue;");
                break;
            case "timespan":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.AppendLine("System.TimeSpan.TryParse(__strValue, out var __timeSpanValue);");
                sb.Append(indent);
                sb.Append(dictVar);
                sb.Append("[");
                sb.Append(keyVar);
                sb.AppendLine("] = __timeSpanValue;");
                break;
            case "enum":
                sb.Append(indent);
                sb.AppendLine("var __rawBytes = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __strValue = System.Text.Encoding.UTF8.GetString(__rawBytes);");
                sb.Append(indent);
                sb.Append("System.Enum.TryParse<");
                sb.Append(prop.ElementTypeName);
                sb.AppendLine(">(__strValue, out var __enumValue);");
                sb.Append(indent);
                sb.Append(dictVar);
                sb.Append("[");
                sb.Append(keyVar);
                sb.AppendLine("] = __enumValue;");
                break;
            case "object":
            {
                var sn = ShortName(prop.ElementTypeName!);
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.Null)");
                sb.Append(indent);
                sb.Append("    ");
                sb.Append(dictVar);
                sb.Append("[");
                sb.Append(keyVar);
                sb.AppendLine("] = default!;");
                sb.Append(indent);
                sb.AppendLine("else");
                sb.Append(indent);
                sb.Append("    ");
                sb.Append(dictVar);
                sb.Append("[");
                sb.Append(keyVar);
                sb.Append("] = ");
                sb.Append(sn);
                sb.AppendLine("JsonInner.Deserialize(ref reader);");
                break;
            }
            default:
                sb.Append(indent);
                sb.Append(dictVar);
                sb.Append("[");
                sb.Append(keyVar);
                sb.AppendLine("] = Encoding.UTF8.GetString(reader.GetStringRaw());");
                break;
        }
    }

    // ── Registration emission ──

    private static void EmitRegistration(StringBuilder sb, TypeInfo type)
    {
        var typeRef = string.IsNullOrEmpty(type.Namespace)
            ? type.Name
            : $"global::{type.Namespace}.{type.Name}";
        sb.Append("file static class ");
        sb.Append(type.Name);
        sb.AppendLine("SerDeRegistration");
        sb.AppendLine("{");
        sb.AppendLine("    [System.Runtime.CompilerServices.ModuleInitializer]");
        sb.AppendLine("    internal static void Register()");
        sb.AppendLine("    {");
        sb.Append("        global::PicoJson.JsonSerializer.Register<");
        sb.Append(typeRef);
        sb.AppendLine(">(");
        sb.Append("            new ");
        sb.Append(type.Name);
        sb.AppendLine("JsonSerializer(),");
        sb.Append("            new ");
        sb.Append(type.Name);
        sb.AppendLine("JsonDeserializer());");
        sb.AppendLine("    }");
        sb.AppendLine("}");
    }

    // ── Data types ──

    internal readonly record struct TypeInfo(
        string FullyQualifiedName,
        string Namespace,
        string Name,
        ImmutableArray<PropertyInfo> Properties
    )
    {
        public bool Equals(TypeInfo other) =>
            FullyQualifiedName == other.FullyQualifiedName
            && Namespace == other.Namespace
            && Name == other.Name
            && Properties.SequenceEqual(other.Properties);

        public override int GetHashCode()
        {
            var hash = FullyQualifiedName.GetHashCode();
            hash = (hash * 397) ^ Namespace.GetHashCode();
            hash = (hash * 397) ^ Name.GetHashCode();
            foreach (var p in Properties)
                hash = (hash * 397) ^ p.GetHashCode();
            return hash;
        }
    }

    internal readonly record struct PropertyInfo(
        string Name,
        string JsonName,
        string TypeKind,
        string TypeFullName,
        bool IsNullable,
        string? ElementTypeKind,
        string? ElementTypeName,
        string? KeyTypeKind,
        string? KeyTypeName,
        ImmutableArray<PropertyInfo> NestedProperties,
        string? ConverterTypeFullName,
        string? DateTimeFormat = null
    );
}
