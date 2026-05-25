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

        // Match both explicit generic calls (Serialize<Foo>) and inferred calls (Serialize(foo))
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

        // Must be a method on PicoJson.JsonSerializer
        if (
            method.ContainingType.Name != "JsonSerializer"
            || method.ContainingType.ContainingNamespace?.ToDisplayString() != "PicoJson"
        )
            return null;

        if (method.TypeArguments.Length != 1)
            return null;

        var typeArg = method.TypeArguments[0];

        // Skip built-in / special types
        if (typeArg.SpecialType != SpecialType.None)
            return null;

        if (typeArg is not INamedTypeSymbol namedType)
            return null;

        // Skip nested types (e.g. test fixture inner classes)
        if (namedType.ContainingType is not null)
            return null;

        var ns = namedType.ContainingNamespace?.ToDisplayString() ?? "";
        // Roslyn returns "<global namespace>" for the global namespace — normalize to empty
        if (ns == "<global namespace>")
            ns = "";
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

            var (typeKind, isNullable, innerTypeSymbol) = GetTypeKind(prop.Type);
            if (typeKind is null)
                continue;

            string? elementTypeKind = null;
            string? elementTypeName = null;
            string? keyTypeKind = null;
            string? keyTypeName = null;
            PropertyInfo[] nestedProperties = [];

            if (typeKind is "list" or "array")
            {
                ITypeSymbol? elementType;
                if (prop.Type is IArrayTypeSymbol arrType)
                    elementType = arrType.ElementType;
                else if (prop.Type is INamedTypeSymbol ntsElem && ntsElem.TypeArguments.Length == 1)
                    elementType = ntsElem.TypeArguments[0];
                else
                    continue;

                var (ek, _, ein) = GetTypeKind(elementType);
                if (ek is null)
                    continue;
                elementTypeKind = ek;
                elementTypeName = MapTypeKindToName(ek, elementType);
                if (ek is "object" && elementType is INamedTypeSymbol eNtsObj)
                    nestedProperties = ExtractNestedProperties(eNtsObj);
            }
            else if (typeKind is "dict")
            {
                if (prop.Type is INamedTypeSymbol ntsDict && ntsDict.TypeArguments.Length == 2)
                {
                    var keyType = ntsDict.TypeArguments[0];
                    var valType = ntsDict.TypeArguments[1];
                    var (kk, _, _) = GetTypeKind(keyType);
                    var (vk, _, vin) = GetTypeKind(valType);
                    if (kk is null || vk is null)
                        continue;
                    keyTypeKind = kk;
                    keyTypeName = MapTypeKindToName(kk, keyType);
                    elementTypeKind = vk;
                    elementTypeName = MapTypeKindToName(vk, valType);
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

            var jsonName = GetJsonPropertyName(prop) ?? prop.Name;
            var converterTypeFullName = GetJsonConverterType(prop);
            var typeFullName = prop.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            properties.Add(
                new PropertyInfo(
                    prop.Name,
                    jsonName,
                    typeKind,
                    typeFullName,
                    isNullable,
                    elementTypeKind,
                    elementTypeName,
                    keyTypeKind,
                    keyTypeName,
                    nestedProperties,
                    converterTypeFullName
                )
            );
        }

        return new TypeInfo(
            namedType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            ns,
            namedType.Name,
            properties.ToArray()
        );
    }

    private static (string? Kind, bool IsNullable, ITypeSymbol? InnerType) GetTypeKind(
        ITypeSymbol type
    )
    {
        // Nullable<T>
        if (
            type is INamedTypeSymbol
            {
                OriginalDefinition.SpecialType: SpecialType.System_Nullable_T,
            } ntsNullable
        )
        {
            var inner = ntsNullable.TypeArguments[0];
            var (innerKind, _, _) = GetTypeKind(inner);
            return (innerKind, true, inner);
        }

        // T[]
        if (type is IArrayTypeSymbol)
            return ("array", false, null);

        // List<T>
        if (
            type is INamedTypeSymbol ntsList
            && ntsList.Name == "List"
            && ntsList.TypeArguments.Length == 1
        )
        {
            var ns = ntsList.ContainingNamespace;
            if (ns?.Name == "Generic")
            {
                var parentNs = ns.ContainingNamespace;
                if (
                    parentNs?.Name == "Collections"
                    && parentNs.ContainingNamespace?.ContainingNamespace?.IsGlobalNamespace == true
                )
                {
                    return ("list", false, null);
                }
            }
        }

        // Dictionary<K,V>
        if (
            type is INamedTypeSymbol ntsDict
            && ntsDict.Name == "Dictionary"
            && ntsDict.TypeArguments.Length == 2
        )
        {
            var ns = ntsDict.ContainingNamespace;
            if (ns?.Name == "Generic")
            {
                var parentNs = ns.ContainingNamespace;
                if (
                    parentNs?.Name == "Collections"
                    && parentNs.ContainingNamespace?.ContainingNamespace?.IsGlobalNamespace == true
                )
                {
                    return ("dict", false, null);
                }
            }
        }

        string? kind = type.SpecialType switch
        {
            SpecialType.System_String => "string",
            SpecialType.System_Int32 => "int32",
            SpecialType.System_Int64 => "int64",
            SpecialType.System_Double => "float64",
            SpecialType.System_Single => "float64",
            SpecialType.System_Boolean => "boolean",
            SpecialType.System_DateTime => "datetime",
            SpecialType.System_Decimal => "decimal",
            _
                => type switch
                {
                    INamedTypeSymbol { TypeKind: TypeKind.Enum } => "enum",
                    INamedTypeSymbol { Name: "Guid", ContainingNamespace.Name: "System" } => "guid",
                    INamedTypeSymbol { Name: "DateOnly", ContainingNamespace.Name: "System" }
                        => "dateonly",
                    INamedTypeSymbol { Name: "TimeOnly", ContainingNamespace.Name: "System" }
                        => "timeonly",
                    INamedTypeSymbol { Name: "TimeSpan", ContainingNamespace.Name: "System" }
                        => "timespan",
                    _ => null,
                },
        };

        // Nested complex types (classes/structs with public properties)
        if (
            kind is null
            && type is INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Struct } ntsObj
        )
        {
            // Check it has at least one public writable property (not just an opaque object)
            foreach (var member in ntsObj.GetMembers())
            {
                if (
                    member
                        is IPropertySymbol
                        {
                            DeclaredAccessibility: Accessibility.Public,
                            IsStatic: false,
                            IsIndexer: false
                        } ps
                    && ps.GetMethod is not null
                    && !(ps.IsReadOnly && ps.SetMethod is null)
                )
                {
                    return ("object", false, null);
                }
            }
        }

        return (kind, false, null);
    }

    private static string MapTypeKindToName(string kind, ITypeSymbol type) =>
        kind switch
        {
            "string" => "string",
            "int32" => "int",
            "int64" => "long",
            "float64" => "double",
            "boolean" => "bool",
            "datetime" => "System.DateTime",
            "dateonly" => "System.DateOnly",
            "timeonly" => "System.TimeOnly",
            "timespan" => "System.TimeSpan",
            "guid" => "System.Guid",
            "decimal" => "decimal",
            "enum" => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            "object" => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            _ => "object",
        };

    private static PropertyInfo[] ExtractNestedProperties(INamedTypeSymbol type)
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

            var (typeKind, isNullable, innerTypeSymbol) = GetTypeKind(prop.Type);
            if (typeKind is null)
                continue;

            string? elementTypeKind = null;
            string? elementTypeName = null;
            string? keyTypeKind = null;
            string? keyTypeName = null;
            PropertyInfo[] nestedProperties = [];

            if (typeKind is "list" or "array")
            {
                ITypeSymbol? elementType;
                if (prop.Type is IArrayTypeSymbol arrType)
                    elementType = arrType.ElementType;
                else if (prop.Type is INamedTypeSymbol ntsElem && ntsElem.TypeArguments.Length == 1)
                    elementType = ntsElem.TypeArguments[0];
                else
                    continue;

                var (ek, _, ein) = GetTypeKind(elementType);
                if (ek is null)
                    continue;
                elementTypeKind = ek;
                elementTypeName = MapTypeKindToName(ek, elementType);
                if (ek is "object" && elementType is INamedTypeSymbol eNtsObj)
                    nestedProperties = ExtractNestedProperties(eNtsObj);
            }
            else if (typeKind is "dict")
            {
                if (prop.Type is INamedTypeSymbol ntsDict && ntsDict.TypeArguments.Length == 2)
                {
                    var keyType = ntsDict.TypeArguments[0];
                    var valType = ntsDict.TypeArguments[1];
                    var (kk, _, _) = GetTypeKind(keyType);
                    var (vk, _, vin) = GetTypeKind(valType);
                    if (kk is null || vk is null)
                        continue;
                    keyTypeKind = kk;
                    keyTypeName = MapTypeKindToName(kk, keyType);
                    elementTypeKind = vk;
                    elementTypeName = MapTypeKindToName(vk, valType);
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

            var jsonName = GetJsonPropertyName(prop) ?? prop.Name;
            var converterTypeFullName = GetJsonConverterType(prop);
            var typeFullName = prop.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            list.Add(
                new PropertyInfo(
                    prop.Name,
                    jsonName,
                    typeKind,
                    typeFullName,
                    isNullable,
                    elementTypeKind,
                    elementTypeName,
                    keyTypeKind,
                    keyTypeName,
                    nestedProperties,
                    converterTypeFullName
                )
            );
        }
        return list.ToArray();
    }

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

    private static void GenerateAll(SourceProductionContext spc, ImmutableArray<TypeInfo> types)
    {
        var seen = new HashSet<string>();
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

    private static string GenerateTypeCode(TypeInfo type)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Buffers;");
        sb.AppendLine("using System.Text;");
        sb.AppendLine("using PicoSerDe.Abs;");
        sb.AppendLine("using PicoJson;");

        if (!string.IsNullOrEmpty(type.Namespace))
        {
            sb.AppendLine();
            sb.Append("namespace ");
            sb.Append(type.Namespace);
            sb.AppendLine(";");
        }

        sb.AppendLine();

        // ---------- Serializer ----------
        AppendSerializer(sb, type);

        sb.AppendLine();

        // ---------- Deserializer ----------
        AppendDeserializer(sb, type);

        sb.AppendLine();

        // ---------- Registration ----------
        AppendRegistration(sb, type);

        return sb.ToString();
    }

    private static void AppendSerializer(StringBuilder sb, TypeInfo type)
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
            var u8Name = $"\"{prop.JsonName}\"u8";

            sb.Append("            jw.WritePropertyName(");
            sb.Append(u8Name);
            sb.AppendLine(");");

            if (prop.IsNullable)
            {
                sb.Append("            if (value.");
                sb.Append(prop.Name);
                sb.AppendLine(".HasValue)");
                sb.AppendLine("            {");
            }

            AppendSerializerValue(sb, prop, isNullableAccess: prop.IsNullable);

            if (prop.IsNullable)
            {
                sb.AppendLine("            }");
                sb.AppendLine("            else");
                sb.AppendLine("                jw.WriteNull();");
            }
        }

        sb.AppendLine("            jw.WriteEndObject();");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
    }

    private static void AppendSerializerValue(
        StringBuilder sb,
        PropertyInfo prop,
        bool isNullableAccess
    )
    {
        var accessor = isNullableAccess ? $"value.{prop.Name}.Value" : $"value.{prop.Name}";

        // Check for custom converter
        if (prop.ConverterTypeFullName is not null)
        {
            sb.Append("            var __conv = new ");
            sb.Append(prop.ConverterTypeFullName);
            sb.AppendLine("();");
            sb.Append("            __conv.Write(writer, ");
            sb.Append(accessor);
            sb.AppendLine(");");
            return;
        }

        switch (prop.TypeKind)
        {
            case "string":
                sb.Append("            jw.WriteString(Encoding.UTF8.GetBytes(");
                sb.Append(accessor);
                sb.AppendLine("));");
                break;
            case "int32":
            case "int64":
            case "float64":
                sb.Append("            jw.WriteNumber(");
                sb.Append(accessor);
                sb.AppendLine(");");
                break;
            case "boolean":
                sb.Append("            jw.WriteBoolean(");
                sb.Append(accessor);
                sb.AppendLine(");");
                break;
            case "datetime":
                sb.Append("            var __iso = ");
                sb.Append(accessor);
                sb.AppendLine(".ToString(\"O\");");
                sb.AppendLine("            jw.WriteString(Encoding.UTF8.GetBytes(__iso));");
                break;
            case "dateonly":
                sb.Append("            var __d = ");
                sb.Append(accessor);
                sb.AppendLine(".ToString(\"O\");");
                sb.AppendLine("            jw.WriteString(Encoding.UTF8.GetBytes(__d));");
                break;
            case "timeonly":
                sb.Append("            var __t = ");
                sb.Append(accessor);
                sb.AppendLine(".ToString(\"O\");");
                sb.AppendLine("            jw.WriteString(Encoding.UTF8.GetBytes(__t));");
                break;
            case "timespan":
                sb.Append("            var __ts = ");
                sb.Append(accessor);
                sb.AppendLine(".ToString();");
                sb.AppendLine("            jw.WriteString(Encoding.UTF8.GetBytes(__ts));");
                break;
            case "guid":
            case "enum":
                sb.Append("            jw.WriteString(Encoding.UTF8.GetBytes(");
                sb.Append(accessor);
                sb.AppendLine(".ToString()));");
                break;
            case "decimal":
                sb.Append("            jw.WriteString(Encoding.UTF8.GetBytes(");
                sb.Append(accessor);
                sb.AppendLine(".ToString(System.Globalization.CultureInfo.InvariantCulture)));");
                break;
            case "list":
            case "array":
                sb.Append("            jw.WriteStartArray();");
                sb.AppendLine();
                sb.Append("            foreach (var __item in ");
                sb.Append(accessor);
                sb.AppendLine(")");
                sb.AppendLine("            {");
                AppendSerializerElementValue(
                    sb,
                    prop.ElementTypeKind!,
                    indent: "                ",
                    nestedProperties: prop.NestedProperties
                );
                sb.AppendLine("            }");
                sb.AppendLine("            jw.WriteEndArray();");
                break;
            case "dict":
                sb.AppendLine("            jw.WriteStartObject();");
                sb.Append("            foreach (var __kvp in ");
                sb.Append(accessor);
                sb.AppendLine(")");
                sb.AppendLine("            {");
                sb.Append("                jw.WritePropertyName(Encoding.UTF8.GetBytes(__kvp.Key");
                if (prop.KeyTypeKind == "string")
                    sb.AppendLine("));");
                else
                    sb.AppendLine(".ToString()));");
                sb.Append("                ");
                AppendSerializerElementValue(
                    sb,
                    prop.ElementTypeKind!,
                    indent: "                ",
                    accessor: "__kvp.Value",
                    nestedProperties: prop.NestedProperties
                );
                sb.AppendLine("            }");
                sb.AppendLine("            jw.WriteEndObject();");
                break;
            case "object":
                sb.Append("            if (");
                sb.Append(accessor);
                sb.AppendLine(" == null) jw.WriteNull();");
                sb.AppendLine("            else");
                sb.AppendLine("            {");
                sb.AppendLine("            jw.WriteStartObject();");
                AppendNestedSerializerProperties(sb, prop.NestedProperties, accessor);
                sb.AppendLine("            jw.WriteEndObject();");
                sb.AppendLine("            }");
                break;
        }
    }

    private static void AppendNestedSerializerProperties(
        StringBuilder sb,
        PropertyInfo[] props,
        string prefix
    )
    {
        foreach (var np in props)
        {
            var npAcc = $"{prefix}.{np.Name}";
            sb.Append("                jw.WritePropertyName(\"");
            sb.Append(np.JsonName);
            sb.AppendLine("\"u8);");

            var needNullableWrap = np.IsNullable && np.TypeKind != "object";
            if (needNullableWrap)
            {
                sb.Append("                if (");
                sb.Append(npAcc);
                sb.AppendLine(".HasValue)");
                sb.AppendLine("                {");
                npAcc = $"{npAcc}.Value";
            }

            if (np.ConverterTypeFullName is not null)
            {
                sb.Append("                var __nc = new ");
                sb.Append(np.ConverterTypeFullName);
                sb.AppendLine("();");
                sb.Append("                __nc.Write(writer, ");
                sb.Append(npAcc);
                sb.AppendLine(");");
                if (needNullableWrap)
                {
                    sb.AppendLine("                }");
                    sb.AppendLine("                else");
                    sb.AppendLine("                    jw.WriteNull();");
                }
                continue;
            }

            switch (np.TypeKind)
            {
                case "string":
                    sb.Append("                jw.WriteString(Encoding.UTF8.GetBytes(");
                    sb.Append(npAcc);
                    sb.AppendLine("));");
                    break;
                case "int32":
                case "int64":
                case "float64":
                    sb.Append("                jw.WriteNumber(");
                    sb.Append(npAcc);
                    sb.AppendLine(");");
                    break;
                case "boolean":
                    sb.Append("                jw.WriteBoolean(");
                    sb.Append(npAcc);
                    sb.AppendLine(");");
                    break;
                case "object":
                    sb.Append("                if (");
                    sb.Append(npAcc);
                    sb.AppendLine(" == null) jw.WriteNull();");
                    sb.AppendLine("                else");
                    sb.AppendLine("                {");
                    sb.AppendLine("                jw.WriteStartObject();");
                    AppendNestedSerializerProperties(sb, np.NestedProperties, npAcc);
                    sb.AppendLine("                jw.WriteEndObject();");
                    sb.AppendLine("                }");
                    break;
                case "list":
                case "array":
                    sb.AppendLine("                jw.WriteStartArray();");
                    sb.Append("                foreach (var __ni in ");
                    sb.Append(npAcc);
                    sb.AppendLine(")");
                    sb.AppendLine("                {");
                    AppendSerializerElementValue(
                        sb,
                        np.ElementTypeKind!,
                        indent: "                    ",
                        accessor: "__ni",
                        nestedProperties: np.NestedProperties
                    );
                    sb.AppendLine("                }");
                    sb.AppendLine("                jw.WriteEndArray();");
                    break;
                case "dict":
                    sb.AppendLine("                jw.WriteStartObject();");
                    sb.Append("                foreach (var __nk in ");
                    sb.Append(npAcc);
                    sb.AppendLine(")");
                    sb.AppendLine("                {");
                    sb.Append(
                        "                    jw.WritePropertyName(Encoding.UTF8.GetBytes(__nk.Key"
                    );
                    if (np.KeyTypeKind == "string")
                        sb.AppendLine("));");
                    else
                        sb.AppendLine(".ToString()));");
                    sb.Append("                    ");
                    AppendSerializerElementValue(
                        sb,
                        np.ElementTypeKind!,
                        indent: "                    ",
                        accessor: "__nk.Value",
                        nestedProperties: np.NestedProperties
                    );
                    sb.AppendLine("                }");
                    sb.AppendLine("                jw.WriteEndObject();");
                    break;
                case "datetime":
                    sb.Append("                var __dt = ");
                    sb.Append(npAcc);
                    sb.AppendLine(".ToString(\"O\");");
                    sb.AppendLine("                jw.WriteString(Encoding.UTF8.GetBytes(__dt));");
                    break;
                case "dateonly":
                    sb.Append("                var __d = ");
                    sb.Append(npAcc);
                    sb.AppendLine(".ToString(\"O\");");
                    sb.AppendLine("                jw.WriteString(Encoding.UTF8.GetBytes(__d));");
                    break;
                case "timeonly":
                    sb.Append("                var __t = ");
                    sb.Append(npAcc);
                    sb.AppendLine(".ToString(\"O\");");
                    sb.AppendLine("                jw.WriteString(Encoding.UTF8.GetBytes(__t));");
                    break;
                case "timespan":
                    sb.Append("                var __ts = ");
                    sb.Append(npAcc);
                    sb.AppendLine(".ToString();");
                    sb.AppendLine("                jw.WriteString(Encoding.UTF8.GetBytes(__ts));");
                    break;
                case "guid":
                case "enum":
                    sb.Append("                jw.WriteString(Encoding.UTF8.GetBytes(");
                    sb.Append(npAcc);
                    sb.AppendLine(".ToString()));");
                    break;
                case "decimal":
                    sb.Append("                jw.WriteString(Encoding.UTF8.GetBytes(");
                    sb.Append(npAcc);
                    sb.AppendLine(
                        ".ToString(System.Globalization.CultureInfo.InvariantCulture)));"
                    );
                    break;
                default:
                    sb.Append("                jw.WriteString(Encoding.UTF8.GetBytes(");
                    sb.Append(npAcc);
                    sb.AppendLine(".ToString()));");
                    break;
            }

            if (needNullableWrap)
            {
                sb.AppendLine("                }");
                sb.AppendLine("                else");
                sb.AppendLine("                    jw.WriteNull();");
            }
        }
    }

    private static void AppendSerializerElementValue(
        StringBuilder sb,
        string elementKind,
        string indent,
        string accessor = "__item",
        PropertyInfo[]? nestedProperties = null
    )
    {
        switch (elementKind)
        {
            case "string":
                sb.Append(indent);
                sb.Append("jw.WriteString(Encoding.UTF8.GetBytes(");
                sb.Append(accessor);
                sb.AppendLine("));");
                break;
            case "int32":
                sb.Append(indent);
                sb.Append("jw.WriteNumber(");
                sb.Append(accessor);
                sb.AppendLine(");");
                break;
            case "int64":
                sb.Append(indent);
                sb.Append("jw.WriteNumber(");
                sb.Append(accessor);
                sb.AppendLine(");");
                break;
            case "float64":
                sb.Append(indent);
                sb.Append("jw.WriteNumber(");
                sb.Append(accessor);
                sb.AppendLine(");");
                break;
            case "boolean":
                sb.Append(indent);
                sb.Append("jw.WriteBoolean(");
                sb.Append(accessor);
                sb.AppendLine(");");
                break;
            case "datetime":
                sb.Append(indent);
                sb.Append("jw.WriteString(Encoding.UTF8.GetBytes(");
                sb.Append(accessor);
                sb.AppendLine(".ToString(\"O\")));");
                break;
            case "dateonly":
                sb.Append(indent);
                sb.Append("jw.WriteString(Encoding.UTF8.GetBytes(");
                sb.Append(accessor);
                sb.AppendLine(".ToString(\"O\")));");
                break;
            case "timeonly":
                sb.Append(indent);
                sb.Append("jw.WriteString(Encoding.UTF8.GetBytes(");
                sb.Append(accessor);
                sb.AppendLine(".ToString(\"O\")));");
                break;
            case "timespan":
                sb.Append(indent);
                sb.Append("jw.WriteString(Encoding.UTF8.GetBytes(");
                sb.Append(accessor);
                sb.AppendLine(".ToString()));");
                break;
            case "guid":
            case "enum":
                sb.Append(indent);
                sb.Append("jw.WriteString(Encoding.UTF8.GetBytes(");
                sb.Append(accessor);
                sb.AppendLine(".ToString()));");
                break;
            case "decimal":
                sb.Append(indent);
                sb.Append("jw.WriteString(Encoding.UTF8.GetBytes(");
                sb.Append(accessor);
                sb.AppendLine(".ToString(System.Globalization.CultureInfo.InvariantCulture)));");
                break;
            case "object":
                sb.Append(indent);
                sb.Append("jw.WriteStartObject();");
                sb.AppendLine();
                AppendNestedSerializerProperties(sb, nestedProperties!, accessor);
                sb.Append(indent);
                sb.AppendLine("jw.WriteEndObject();");
                break;
            default:
                sb.Append(indent);
                sb.Append("jw.WriteString(Encoding.UTF8.GetBytes(");
                sb.Append(accessor);
                sb.AppendLine(".ToString()));");
                break;
        }
    }

    private static void AppendDeserializer(StringBuilder sb, TypeInfo type)
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
        sb.AppendLine("                var p = reader.GetStringRaw();");
        sb.AppendLine("                reader.Read();");
        sb.AppendLine("                var __pn = Encoding.UTF8.GetString(p);");

        for (var i = 0; i < type.Properties.Length; i++)
        {
            var prop = type.Properties[i];
            var keyword = i == 0 ? "if" : "else if";

            sb.Append("                ");
            sb.Append(keyword);
            sb.Append(" (__pn.Equals(\"");
            sb.Append(prop.JsonName);
            sb.AppendLine("\", StringComparison.OrdinalIgnoreCase))");

            if (prop.IsNullable)
            {
                sb.AppendLine("                {");
                sb.AppendLine("                    if (reader.TokenType == TokenType.Null)");
                sb.Append("                        obj.");
                sb.Append(prop.Name);
                sb.AppendLine(" = null;");
                sb.AppendLine("                    else");
                sb.AppendLine("                    {");
                AppendDeserializerValue(sb, prop, indent: "                        ");
                sb.AppendLine("                    }");
                sb.AppendLine("                }");
            }
            else
            {
                sb.AppendLine("                {");
                AppendDeserializerValue(sb, prop, indent: "                    ");
                sb.AppendLine("                }");
            }
        }

        if (type.Properties.Length > 0)
        {
            sb.AppendLine("                else reader.TrySkip();");
        }
        else
        {
            sb.AppendLine("                reader.TrySkip();");
        }

        sb.AppendLine("            }");
        sb.AppendLine("            return obj;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
    }

    private static void AppendDeserializerValue(StringBuilder sb, PropertyInfo prop, string indent)
    {
        if (prop.ConverterTypeFullName is not null)
        {
            sb.Append(indent);
            sb.AppendLine("var __converter = new ");
            sb.Append(prop.ConverterTypeFullName);
            sb.AppendLine("();");
            sb.Append(indent);
            sb.AppendLine("var __craw = reader.GetStringRaw();");
            sb.Append(indent);
            sb.Append("obj.");
            sb.Append(prop.Name);
            sb.AppendLine(" = __converter.Read(__craw);");
            return;
        }

        switch (prop.TypeKind)
        {
            case "string":
                sb.Append(indent);
                sb.Append("obj.");
                sb.Append(prop.Name);
                sb.AppendLine(" = Encoding.UTF8.GetString(reader.GetStringRaw());");
                break;
            case "int32":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetInt32(out var __v);");
                sb.Append(indent);
                sb.Append("obj.");
                sb.Append(prop.Name);
                sb.AppendLine(" = __v;");
                break;
            case "int64":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetInt64(out var __v);");
                sb.Append(indent);
                sb.Append("obj.");
                sb.Append(prop.Name);
                sb.AppendLine(" = __v;");
                break;
            case "float64":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetFloat64(out var __v);");
                sb.Append(indent);
                sb.Append("obj.");
                sb.Append(prop.Name);
                sb.AppendLine(" = __v;");
                break;
            case "boolean":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetBool(out var __v);");
                sb.Append(indent);
                sb.Append("obj.");
                sb.Append(prop.Name);
                sb.AppendLine(" = __v;");
                break;
            case "datetime":
                sb.Append(indent);
                sb.AppendLine("var __raw = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __str = Encoding.UTF8.GetString(__raw);");
                sb.Append(indent);
                sb.AppendLine(
                    "System.DateTime.TryParse(__str, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var __dt);"
                );
                sb.Append(indent);
                sb.Append("obj.");
                sb.Append(prop.Name);
                sb.AppendLine(" = __dt;");
                break;
            case "guid":
                sb.Append(indent);
                sb.AppendLine("var __raw = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("System.Guid.TryParse(__raw, out var __g);");
                sb.Append(indent);
                sb.Append("obj.");
                sb.Append(prop.Name);
                sb.AppendLine(" = __g;");
                break;
            case "decimal":
                sb.Append(indent);
                sb.AppendLine("var __raw = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine(
                    "decimal.TryParse(__raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var __d);"
                );
                sb.Append(indent);
                sb.Append("obj.");
                sb.Append(prop.Name);
                sb.AppendLine(" = __d;");
                break;
            case "dateonly":
                sb.Append(indent);
                sb.AppendLine("var __raw = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __str = Encoding.UTF8.GetString(__raw);");
                sb.Append(indent);
                sb.AppendLine("System.DateOnly.TryParse(__str, out var __dv);");
                sb.Append(indent);
                sb.Append("obj.");
                sb.Append(prop.Name);
                sb.AppendLine(" = __dv;");
                break;
            case "timeonly":
                sb.Append(indent);
                sb.AppendLine("var __raw = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __str = Encoding.UTF8.GetString(__raw);");
                sb.Append(indent);
                sb.AppendLine("System.TimeOnly.TryParse(__str, out var __tv);");
                sb.Append(indent);
                sb.Append("obj.");
                sb.Append(prop.Name);
                sb.AppendLine(" = __tv;");
                break;
            case "timespan":
                sb.Append(indent);
                sb.AppendLine("var __raw = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __str = Encoding.UTF8.GetString(__raw);");
                sb.Append(indent);
                sb.AppendLine("System.TimeSpan.TryParse(__str, out var __ts);");
                sb.Append(indent);
                sb.Append("obj.");
                sb.Append(prop.Name);
                sb.AppendLine(" = __ts;");
                break;
            case "enum":
                sb.Append(indent);
                sb.AppendLine("var __raw = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __es = System.Text.Encoding.UTF8.GetString(__raw);");
                sb.Append(indent);
                sb.Append("System.Enum.TryParse<");
                sb.Append(prop.TypeFullName);
                sb.AppendLine(">(__es, out var __ev);");
                sb.Append(indent);
                sb.Append("obj.");
                sb.Append(prop.Name);
                sb.AppendLine(" = __ev;");
                break;
            case "list":
            case "array":
                sb.Append(indent);
                sb.Append("var __items = new System.Collections.Generic.List<");
                sb.Append(prop.ElementTypeName);
                sb.AppendLine(">();");
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.ArrayStart)");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.AppendLine(
                    "    while (reader.Read() && reader.TokenType != TokenType.ArrayEnd)"
                );
                sb.Append(indent);
                sb.AppendLine("    {");
                sb.Append(indent);
                AppendDeserializerElementValue(
                    sb,
                    prop.ElementTypeKind!,
                    indent + "        ",
                    nestedProperties: prop.NestedProperties,
                    elementTypeName: prop.ElementTypeName
                );
                sb.Append(indent);
                sb.AppendLine("    }");
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.Append("obj.");
                sb.Append(prop.Name);
                sb.Append(" = ");
                if (prop.TypeKind == "array")
                    sb.Append("__items.ToArray()");
                else
                    sb.Append("__items");
                sb.AppendLine(";");
                break;
            case "dict":
                sb.Append(indent);
                sb.Append("var __dict = new System.Collections.Generic.Dictionary<");
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
                sb.Append(indent);
                sb.AppendLine(
                    "        var __key = System.Text.Encoding.UTF8.GetString(reader.GetStringRaw());"
                );
                sb.Append(indent);
                sb.AppendLine("        reader.Read();");
                sb.Append(indent);
                AppendDeserializerElementValue(
                    sb,
                    prop.ElementTypeKind!,
                    indent + "        ",
                    target: "__dict[__key]",
                    useAssignment: true,
                    nestedProperties: prop.NestedProperties,
                    elementTypeName: prop.ElementTypeName
                );
                sb.Append(indent);
                sb.AppendLine("    }");
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.Append("obj.");
                sb.Append(prop.Name);
                sb.AppendLine(" = __dict;");
                break;
            case "object":
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.Null)");
                sb.Append(indent);
                sb.Append("    obj.");
                sb.Append(prop.Name);
                sb.AppendLine(" = null;");
                sb.Append(indent);
                sb.AppendLine("else");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.Append("    var __o = new ");
                sb.Append(prop.TypeFullName);
                sb.AppendLine("();");
                sb.Append(indent);
                sb.AppendLine(
                    "    while (reader.Read() && reader.TokenType == TokenType.PropertyName)"
                );
                sb.Append(indent);
                sb.AppendLine("    {");
                sb.Append(indent);
                sb.AppendLine("        var __op = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("        reader.Read();");
                AppendNestedDeserializerProperties(
                    sb,
                    prop.NestedProperties,
                    "__o",
                    indent + "        "
                );
                sb.Append(indent);
                sb.AppendLine("        else reader.TrySkip();");
                sb.Append(indent);
                sb.AppendLine("    }");
                sb.Append(indent);
                sb.Append("    obj.");
                sb.Append(prop.Name);
                sb.AppendLine(" = __o;");
                sb.Append(indent);
                sb.AppendLine("}");
                break;
        }
    }

    private static void AppendNestedDeserializerProperties(
        StringBuilder sb,
        PropertyInfo[] props,
        string target,
        string indent,
        string propVar = "__op"
    )
    {
        for (var i = 0; i < props.Length; i++)
        {
            var np = props[i];
            var keyword = i == 0 ? "if" : "else if";
            sb.Append(indent);
            sb.Append(keyword);
            sb.Append(" (Encoding.UTF8.GetString(");
            sb.Append(propVar);
            sb.Append(").Equals(\"");
            sb.Append(np.JsonName);
            sb.AppendLine("\", StringComparison.OrdinalIgnoreCase))");

            if (np.IsNullable && np.TypeKind != "object")
            {
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.AppendLine("    if (reader.TokenType == TokenType.Null)");
                sb.Append(indent);
                sb.Append("        ");
                sb.Append(target);
                sb.Append(".");
                sb.Append(np.Name);
                sb.AppendLine(" = null;");
                sb.Append(indent);
                sb.AppendLine("    else");
                sb.Append(indent);
                sb.AppendLine("    {");
                AppendDeserializerNestedValue(sb, np, target, indent + "        ");
                sb.Append(indent);
                sb.AppendLine("    }");
                sb.Append(indent);
                sb.AppendLine("}");
            }
            else if (np.TypeKind == "object")
            {
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.AppendLine("    if (reader.TokenType == TokenType.Null)");
                sb.Append(indent);
                sb.Append("        ");
                sb.Append(target);
                sb.Append(".");
                sb.Append(np.Name);
                sb.AppendLine(" = null;");
                sb.Append(indent);
                sb.AppendLine("    else");
                sb.Append(indent);
                sb.AppendLine("    {");
                sb.Append(indent);
                sb.Append("        var __no = new ");
                sb.Append(np.TypeFullName);
                sb.AppendLine("();");
                sb.Append(indent);
                sb.AppendLine(
                    "        while (reader.Read() && reader.TokenType == TokenType.PropertyName)"
                );
                sb.Append(indent);
                sb.AppendLine("        {");
                sb.Append(indent);
                sb.AppendLine("            var __np = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("            reader.Read();");
                AppendNestedDeserializerProperties(
                    sb,
                    np.NestedProperties,
                    "__no",
                    indent + "            ",
                    propVar: "__np"
                );
                sb.Append(indent);
                sb.AppendLine("            else reader.TrySkip();");
                sb.Append(indent);
                sb.AppendLine("        }");
                sb.Append(indent);
                sb.Append("        ");
                sb.Append(target);
                sb.Append(".");
                sb.Append(np.Name);
                sb.AppendLine(" = __no;");
                sb.Append(indent);
                sb.AppendLine("    }");
                sb.Append(indent);
                sb.AppendLine("}");
            }
            else
            {
                sb.Append(indent);
                sb.AppendLine("{");
                AppendDeserializerNestedValue(sb, np, target, indent + "    ");
                sb.Append(indent);
                sb.AppendLine("}");
            }
        }
    }

    private static void AppendDeserializerNestedValue(
        StringBuilder sb,
        PropertyInfo prop,
        string target,
        string indent
    )
    {
        if (prop.ConverterTypeFullName is not null)
        {
            sb.Append(indent);
            sb.AppendLine("var __nconverter = new ");
            sb.Append(prop.ConverterTypeFullName);
            sb.AppendLine("();");
            sb.Append(indent);
            sb.AppendLine("var __nraw = reader.GetStringRaw();");
            sb.Append(indent);
            sb.Append(target);
            sb.Append(".");
            sb.Append(prop.Name);
            sb.AppendLine(" = __nconverter.Read(__nraw);");
            return;
        }

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
                sb.AppendLine("reader.TryGetInt32(out var __v);");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __v;");
                break;
            case "int64":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetInt64(out var __v);");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __v;");
                break;
            case "float64":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetFloat64(out var __v);");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __v;");
                break;
            case "boolean":
                sb.Append(indent);
                sb.AppendLine("reader.TryGetBool(out var __v);");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __v;");
                break;
            case "datetime":
                sb.Append(indent);
                sb.AppendLine("var __raw = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __str = Encoding.UTF8.GetString(__raw);");
                sb.Append(indent);
                sb.AppendLine(
                    "System.DateTime.TryParse(__str, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var __dt);"
                );
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __dt;");
                break;
            case "guid":
                sb.Append(indent);
                sb.AppendLine("var __raw = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("System.Guid.TryParse(__raw, out var __g);");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __g;");
                break;
            case "decimal":
                sb.Append(indent);
                sb.AppendLine("var __raw = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine(
                    "decimal.TryParse(__raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var __d);"
                );
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __d;");
                break;
            case "dateonly":
                sb.Append(indent);
                sb.AppendLine("var __raw = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __str = Encoding.UTF8.GetString(__raw);");
                sb.Append(indent);
                sb.AppendLine("System.DateOnly.TryParse(__str, out var __dv);");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __dv;");
                break;
            case "timeonly":
                sb.Append(indent);
                sb.AppendLine("var __raw = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __str = Encoding.UTF8.GetString(__raw);");
                sb.Append(indent);
                sb.AppendLine("System.TimeOnly.TryParse(__str, out var __tv);");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __tv;");
                break;
            case "timespan":
                sb.Append(indent);
                sb.AppendLine("var __raw = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __str = Encoding.UTF8.GetString(__raw);");
                sb.Append(indent);
                sb.AppendLine("System.TimeSpan.TryParse(__str, out var __ts);");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __ts;");
                break;
            case "enum":
                sb.Append(indent);
                sb.AppendLine("var __raw = reader.GetStringRaw();");
                sb.Append(indent);
                sb.AppendLine("var __es = System.Text.Encoding.UTF8.GetString(__raw);");
                sb.Append(indent);
                sb.Append("System.Enum.TryParse<");
                sb.Append(prop.TypeFullName);
                sb.AppendLine(">(__es, out var __ev);");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = __ev;");
                break;
            case "list":
            case "array":
                sb.Append(indent);
                sb.Append("var __nitems = new System.Collections.Generic.List<");
                sb.Append(prop.ElementTypeName);
                sb.AppendLine(">();");
                sb.Append(indent);
                sb.AppendLine("if (reader.TokenType == TokenType.ArrayStart)");
                sb.Append(indent);
                sb.AppendLine("{");
                sb.Append(indent);
                sb.AppendLine(
                    "    while (reader.Read() && reader.TokenType != TokenType.ArrayEnd)"
                );
                sb.Append(indent);
                sb.AppendLine("    {");
                sb.Append(indent);
                AppendDeserializerElementValue(
                    sb,
                    prop.ElementTypeKind!,
                    indent + "        ",
                    target: "__nitems",
                    nestedProperties: prop.NestedProperties,
                    elementTypeName: prop.ElementTypeName
                );
                sb.Append(indent);
                sb.AppendLine("    }");
                sb.Append(indent);
                sb.AppendLine("}");
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.Append(" = ");
                if (prop.TypeKind == "array")
                    sb.Append("__nitems.ToArray()");
                else
                    sb.Append("__nitems");
                sb.AppendLine(";");
                break;
            default:
                sb.Append(indent);
                sb.Append(target);
                sb.Append(".");
                sb.Append(prop.Name);
                sb.AppendLine(" = Encoding.UTF8.GetString(reader.GetStringRaw());");
                break;
        }
    }

    private static void AppendDeserializerElementValue(
        StringBuilder sb,
        string elementKind,
        string indent,
        string target = "__items",
        bool useAssignment = false,
        PropertyInfo[]? nestedProperties = null,
        string? elementTypeName = null
    )
    {
        if (useAssignment)
        {
            switch (elementKind)
            {
                case "string":
                    sb.Append(indent);
                    sb.Append(target);
                    sb.AppendLine(" = System.Text.Encoding.UTF8.GetString(reader.GetStringRaw());");
                    break;
                case "int32":
                    sb.Append(indent);
                    sb.AppendLine("reader.TryGetInt32(out var __ev);");
                    sb.Append(indent);
                    sb.Append(target);
                    sb.AppendLine(" = __ev;");
                    break;
                case "int64":
                    sb.Append(indent);
                    sb.AppendLine("reader.TryGetInt64(out var __ev);");
                    sb.Append(indent);
                    sb.Append(target);
                    sb.AppendLine(" = __ev;");
                    break;
                case "float64":
                    sb.Append(indent);
                    sb.AppendLine("reader.TryGetFloat64(out var __ev);");
                    sb.Append(indent);
                    sb.Append(target);
                    sb.AppendLine(" = __ev;");
                    break;
                case "boolean":
                    sb.Append(indent);
                    sb.AppendLine("reader.TryGetBool(out var __ev);");
                    sb.Append(indent);
                    sb.Append(target);
                    sb.AppendLine(" = __ev;");
                    break;
                case "datetime":
                    sb.Append(indent);
                    sb.AppendLine("var __raw = reader.GetStringRaw();");
                    sb.Append(indent);
                    sb.AppendLine("var __str = Encoding.UTF8.GetString(__raw);");
                    sb.Append(indent);
                    sb.AppendLine(
                        "System.DateTime.TryParse(__str, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var __dt);"
                    );
                    sb.Append(indent);
                    sb.Append(target);
                    sb.AppendLine(" = __dt;");
                    break;
                case "dateonly":
                    sb.Append(indent);
                    sb.AppendLine("var __raw = reader.GetStringRaw();");
                    sb.Append(indent);
                    sb.AppendLine("var __str = Encoding.UTF8.GetString(__raw);");
                    sb.Append(indent);
                    sb.AppendLine("System.DateOnly.TryParse(__str, out var __dv);");
                    sb.Append(indent);
                    sb.Append(target);
                    sb.AppendLine(" = __dv;");
                    break;
                case "timeonly":
                    sb.Append(indent);
                    sb.AppendLine("var __raw = reader.GetStringRaw();");
                    sb.Append(indent);
                    sb.AppendLine("var __str = Encoding.UTF8.GetString(__raw);");
                    sb.Append(indent);
                    sb.AppendLine("System.TimeOnly.TryParse(__str, out var __tv);");
                    sb.Append(indent);
                    sb.Append(target);
                    sb.AppendLine(" = __tv;");
                    break;
                case "timespan":
                    sb.Append(indent);
                    sb.AppendLine("var __raw = reader.GetStringRaw();");
                    sb.Append(indent);
                    sb.AppendLine("var __str = Encoding.UTF8.GetString(__raw);");
                    sb.Append(indent);
                    sb.AppendLine("System.TimeSpan.TryParse(__str, out var __ts);");
                    sb.Append(indent);
                    sb.Append(target);
                    sb.AppendLine(" = __ts;");
                    break;
                case "guid":
                    sb.Append(indent);
                    sb.AppendLine("var __raw = reader.GetStringRaw();");
                    sb.Append(indent);
                    sb.AppendLine("System.Guid.TryParse(__raw, out var __g);");
                    sb.Append(indent);
                    sb.Append(target);
                    sb.AppendLine(" = __g;");
                    break;
                case "decimal":
                    sb.Append(indent);
                    sb.AppendLine("var __raw = reader.GetStringRaw();");
                    sb.Append(indent);
                    sb.AppendLine(
                        "decimal.TryParse(__raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var __d);"
                    );
                    sb.Append(indent);
                    sb.Append(target);
                    sb.AppendLine(" = __d;");
                    break;
                case "enum":
                    sb.Append(indent);
                    sb.AppendLine("var __raw = reader.GetStringRaw();");
                    sb.Append(indent);
                    sb.AppendLine("var __es = System.Text.Encoding.UTF8.GetString(__raw);");
                    sb.Append(indent);
                    sb.Append("System.Enum.TryParse<");
                    sb.Append(elementTypeName ?? "System.Object");
                    sb.AppendLine(">(__es, out var __ev);");
                    sb.Append(indent);
                    sb.Append(target);
                    sb.AppendLine(" = __ev;");
                    break;
                case "object":
                    sb.Append(indent);
                    sb.AppendLine("if (reader.TokenType == TokenType.Null)");
                    sb.Append(indent);
                    sb.Append(target);
                    sb.AppendLine(" = default!;");
                    sb.Append(indent);
                    sb.AppendLine("else");
                    sb.Append(indent);
                    sb.AppendLine("{");
                    sb.Append(indent);
                    sb.Append("    var __oe = new ");
                    sb.Append(elementTypeName ?? "object");
                    sb.AppendLine("();");
                    sb.Append(indent);
                    sb.AppendLine(
                        "    while (reader.Read() && reader.TokenType == TokenType.PropertyName)"
                    );
                    sb.Append(indent);
                    sb.AppendLine("    {");
                    sb.Append(indent);
                    sb.AppendLine("        var __oep = reader.GetStringRaw();");
                    sb.Append(indent);
                    sb.AppendLine("        reader.Read();");
                    AppendNestedDeserializerProperties(
                        sb,
                        nestedProperties ?? [],
                        "__oe",
                        indent + "        ",
                        propVar: "__oep"
                    );
                    sb.Append(indent);
                    sb.AppendLine("        else reader.TrySkip();");
                    sb.Append(indent);
                    sb.AppendLine("    }");
                    sb.Append(indent);
                    sb.Append(target);
                    sb.AppendLine(" = __oe;");
                    sb.Append(indent);
                    sb.AppendLine("}");
                    break;
                default:
                    sb.Append(indent);
                    sb.Append("var __s = reader.GetStringRaw();");
                    sb.AppendLine();
                    sb.Append(indent);
                    sb.Append(target);
                    sb.AppendLine(" = System.Text.Encoding.UTF8.GetString(__s);");
                    break;
            }
        }
        else
        {
            switch (elementKind)
            {
                case "string":
                    sb.Append(indent);
                    sb.Append(target);
                    sb.AppendLine(
                        ".Add(System.Text.Encoding.UTF8.GetString(reader.GetStringRaw()));"
                    );
                    break;
                case "int32":
                    sb.Append(indent);
                    sb.AppendLine("reader.TryGetInt32(out var __ev);");
                    sb.Append(indent);
                    sb.Append(target);
                    sb.AppendLine(".Add(__ev);");
                    break;
                case "int64":
                    sb.Append(indent);
                    sb.AppendLine("reader.TryGetInt64(out var __ev);");
                    sb.Append(indent);
                    sb.Append(target);
                    sb.AppendLine(".Add(__ev);");
                    break;
                case "float64":
                    sb.Append(indent);
                    sb.AppendLine("reader.TryGetFloat64(out var __ev);");
                    sb.Append(indent);
                    sb.Append(target);
                    sb.AppendLine(".Add(__ev);");
                    break;
                case "boolean":
                    sb.Append(indent);
                    sb.AppendLine("reader.TryGetBool(out var __ev);");
                    sb.Append(indent);
                    sb.Append(target);
                    sb.AppendLine(".Add(__ev);");
                    break;
                case "datetime":
                    sb.Append(indent);
                    sb.AppendLine("var __raw = reader.GetStringRaw();");
                    sb.Append(indent);
                    sb.AppendLine("var __str = Encoding.UTF8.GetString(__raw);");
                    sb.Append(indent);
                    sb.AppendLine(
                        "System.DateTime.TryParse(__str, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var __dt);"
                    );
                    sb.Append(indent);
                    sb.Append(target);
                    sb.AppendLine(".Add(__dt);");
                    break;
                case "dateonly":
                    sb.Append(indent);
                    sb.AppendLine("var __raw = reader.GetStringRaw();");
                    sb.Append(indent);
                    sb.AppendLine("var __str = Encoding.UTF8.GetString(__raw);");
                    sb.Append(indent);
                    sb.AppendLine("System.DateOnly.TryParse(__str, out var __dv);");
                    sb.Append(indent);
                    sb.Append(target);
                    sb.AppendLine(".Add(__dv);");
                    break;
                case "timeonly":
                    sb.Append(indent);
                    sb.AppendLine("var __raw = reader.GetStringRaw();");
                    sb.Append(indent);
                    sb.AppendLine("var __str = Encoding.UTF8.GetString(__raw);");
                    sb.Append(indent);
                    sb.AppendLine("System.TimeOnly.TryParse(__str, out var __tv);");
                    sb.Append(indent);
                    sb.Append(target);
                    sb.AppendLine(".Add(__tv);");
                    break;
                case "timespan":
                    sb.Append(indent);
                    sb.AppendLine("var __raw = reader.GetStringRaw();");
                    sb.Append(indent);
                    sb.AppendLine("var __str = Encoding.UTF8.GetString(__raw);");
                    sb.Append(indent);
                    sb.AppendLine("System.TimeSpan.TryParse(__str, out var __ts);");
                    sb.Append(indent);
                    sb.Append(target);
                    sb.AppendLine(".Add(__ts);");
                    break;
                case "guid":
                    sb.Append(indent);
                    sb.AppendLine("var __raw = reader.GetStringRaw();");
                    sb.Append(indent);
                    sb.AppendLine("System.Guid.TryParse(__raw, out var __g);");
                    sb.Append(indent);
                    sb.Append(target);
                    sb.AppendLine(".Add(__g);");
                    break;
                case "decimal":
                    sb.Append(indent);
                    sb.AppendLine("var __raw = reader.GetStringRaw();");
                    sb.Append(indent);
                    sb.AppendLine(
                        "decimal.TryParse(__raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var __d);"
                    );
                    sb.Append(indent);
                    sb.Append(target);
                    sb.AppendLine(".Add(__d);");
                    break;
                case "enum":
                    sb.Append(indent);
                    sb.AppendLine("var __raw = reader.GetStringRaw();");
                    sb.Append(indent);
                    sb.AppendLine("var __es = System.Text.Encoding.UTF8.GetString(__raw);");
                    sb.Append(indent);
                    sb.Append("System.Enum.TryParse<");
                    sb.Append(elementTypeName ?? "System.Object");
                    sb.AppendLine(">(__es, out var __ev);");
                    sb.Append(indent);
                    sb.Append(target);
                    sb.AppendLine(".Add(__ev);");
                    break;
                case "object":
                    sb.Append(indent);
                    sb.AppendLine("{");
                    sb.Append(indent);
                    sb.Append("    var __oe = new ");
                    sb.Append(elementTypeName ?? "object");
                    sb.AppendLine("();");
                    sb.Append(indent);
                    sb.AppendLine(
                        "    while (reader.Read() && reader.TokenType == TokenType.PropertyName)"
                    );
                    sb.Append(indent);
                    sb.AppendLine("    {");
                    sb.Append(indent);
                    sb.AppendLine("        var __oep = reader.GetStringRaw();");
                    sb.Append(indent);
                    sb.AppendLine("        reader.Read();");
                    AppendNestedDeserializerProperties(
                        sb,
                        nestedProperties ?? [],
                        "__oe",
                        indent + "        ",
                        propVar: "__oep"
                    );
                    sb.Append(indent);
                    sb.AppendLine("        else reader.TrySkip();");
                    sb.Append(indent);
                    sb.AppendLine("    }");
                    sb.Append(indent);
                    sb.Append(target);
                    sb.AppendLine(".Add(__oe);");
                    sb.Append(indent);
                    sb.AppendLine("}");
                    break;
                default:
                    sb.Append(indent);
                    sb.AppendLine("var __s = reader.GetStringRaw();");
                    sb.Append(indent);
                    sb.Append(target);
                    sb.AppendLine(".Add(System.Text.Encoding.UTF8.GetString(__s));");
                    break;
            }
        }
    }

    private static void AppendRegistration(StringBuilder sb, TypeInfo type)
    {
        var typeName = type.Name;
        var ns = type.Namespace;
        var typeRef = string.IsNullOrEmpty(ns) ? typeName : $"global::{ns}.{typeName}";

        sb.Append("file static class ");
        sb.Append(typeName);
        sb.AppendLine("SerDeRegistration");
        sb.AppendLine("{");
        sb.AppendLine("    [System.Runtime.CompilerServices.ModuleInitializer]");
        sb.AppendLine("    internal static void Register()");
        sb.AppendLine("    {");
        sb.Append("        global::PicoJson.JsonSerializer._serializers[typeof(");
        sb.Append(typeRef);
        sb.Append(")] = new ");
        sb.Append(typeName);
        sb.AppendLine("JsonSerializer();");
        sb.Append("        global::PicoJson.JsonSerializer._deserializers[typeof(");
        sb.Append(typeRef);
        sb.Append(")] = new ");
        sb.Append(typeName);
        sb.AppendLine("JsonDeserializer();");
        sb.AppendLine("    }");
        sb.AppendLine("}");
    }

    internal readonly record struct TypeInfo(
        string FullyQualifiedName,
        string Namespace,
        string Name,
        PropertyInfo[] Properties
    )
    {
        public bool Equals(TypeInfo other) =>
            FullyQualifiedName == other.FullyQualifiedName
            && Namespace == other.Namespace
            && Name == other.Name
            && Properties.AsSpan().SequenceEqual(other.Properties.AsSpan());

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
        PropertyInfo[] NestedProperties,
        string? ConverterTypeFullName
    );
}
