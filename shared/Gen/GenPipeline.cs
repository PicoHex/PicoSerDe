// The incremental pipeline all five generators run, in ONE place (review C2). Each generator used to
// spell the whole body out — 146 lines per generator, five copies — and the copies had drifted: the
// JSON one added `.Combine(asmName)` to the merged pipeline and set the assembly prefix from it, the
// YAML one renamed every local (`context`→`ctx`, `IsCandidate`→`IsC`, `Transform`→`Tf`). A `diff` of
// the two closest copies (INI vs MessagePack) was five lines: a lambda parameter name, a comment, the
// format attribute's metadata name, the format's value emitter, and a one-line private wrapper.
//
// WHAT IS SHARED is the SHAPE: which Roslyn call builds each pipeline, the order the five type sources
// are merged into one output, how the anonymous-type callback reports its two capability diagnostics,
// and that the assembly prefix is cleared before that merged output runs. That shape must never
// disagree between formats — it decides whether a type is seen at all.
//
// WHAT IS NOT SHARED is WHICH pipelines a format registers, and every literal in them: its format
// attribute's metadata name, its syntax transform, its value emitter, and its two diagnostic
// descriptors (kept per format, so a JSON user is never told about an INI diagnostic). A generator
// that wants four type sources instead of five calls four builders instead of five. Nothing below
// switches on a format tag and no format literal appears here (`CapabilityGuardTests` scans for them).
//
// TWO METHODS, NOT A FLAG, for the one real difference: the merged output of the four text/binary
// formats clears `AssemblyPrefix` and emits, while JSON's merged output DEPENDS ON the compilation
// (`.Combine(assemblyName)`) and sets `__PicoSerDe_<asm>` — only the JSON generator nests its helpers
// under an assembly-scoped namespace. A `bool` would have been a shared DECISION; as two registration
// methods the choice stays in the generator that lives with it.
//
// THE GRAPH IS REPRODUCED EXACTLY, not merely the output bytes: an incremental generator's cache
// granularity IS its provider graph, so the `.Collect()`/`.Combine()` order, the `Select(AddRange)`
// steps and the reuse of ONE assembly-name provider across the anonymous and merged pipelines are
// deliberate.

using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PicoSerDe.Gen;

/// <summary>
/// Every format fact one anonymous-type pipeline needs, in one bundle: the config pair the emitters
/// take, the format's two capability diagnostics and its own value emitter.
/// </summary>
internal readonly record struct AnonPipelineConfig(
    FormatConfig Format,
    AttributeHelpers Attributes,
    AnonFormatConfig AnonFormat,
    DiagnosticDescriptor RequiresCSharp12,
    DiagnosticDescriptor RequiresUnsafe,
    Func<AnonFieldInfo, string, string, string> EmitField
);

/// <summary>
/// The pipeline builders the five PicoSerDe generators compose their <c>Initialize</c> from.
/// </summary>
internal static class GenPipeline
{
    /// <summary>
    /// The cross-format attribute every generator honours, <c>[PicoSerializable]</c>: spelled once
    /// instead of five times, because four of the five copies also spelled it identically and the
    /// fifth could only have differed by being wrong.
    /// </summary>
    private const string SerializableAttributeMetadataName =
        "PicoSerDe.Core.PicoSerializableAttribute";

    /// <summary>The shorthand form <c>[GenerateSerializer(typeof(T))]</c>, honoured by all five.</summary>
    private const string ShorthandAttributeMetadataName =
        "PicoSerDe.Core.GenerateSerializerAttribute";

    /// <summary>The polymorphic marker <c>[PicoDerivedType]</c>, honoured by all five.</summary>
    private const string DerivedTypeAttributeMetadataName =
        "PicoSerDe.Core.PicoDerivedTypeAttribute";

    /// <summary>
    /// Pipeline A — the usage-driven source: a call site of the format's serializer names the type to
    /// generate for. <paramref name="syntaxTransform"/> is the format's own extraction; the predicate —
    /// "could this invocation be one of ours" — is not.
    /// </summary>
    internal static IncrementalValuesProvider<TypeInfo> UsageDriven(
        IncrementalGeneratorInitializationContext context,
        Func<GeneratorSyntaxContext, TypeInfo?> syntaxTransform
    ) =>
        context
            .SyntaxProvider.CreateSyntaxProvider(
                predicate: static (node, _) => GenInfrastructure.IsCandidate(node),
                transform: (ctx, _) => syntaxTransform(ctx)
            )
            .Where(static t => t is not null)
            .Select(static (t, _) => t!.Value);

    /// <summary>Pipeline B — types discovered through <c>[PicoSerializable]</c>.</summary>
    internal static IncrementalValuesProvider<TypeInfo> AttributeDriven(
        IncrementalGeneratorInitializationContext context,
        FormatConfig config,
        AttributeHelpers attrs
    ) => ExpandFromAttribute(context, SerializableAttributeMetadataName, config, attrs);

    /// <summary>Pipeline D — types discovered through <c>[GenerateSerializer(typeof(T))]</c>.</summary>
    internal static IncrementalValuesProvider<TypeInfo> ShorthandAttributeDriven(
        IncrementalGeneratorInitializationContext context,
        FormatConfig config,
        AttributeHelpers attrs
    ) => ExpandFromAttribute(context, ShorthandAttributeMetadataName, config, attrs);

    /// <summary>
    /// Pipeline C — types discovered through the FORMAT's own attribute, e.g. <c>[PicoIniSerializable]</c>.
    /// <paramref name="metadataName"/> is the one literal only the calling generator can know.
    /// </summary>
    internal static IncrementalValuesProvider<TypeInfo> FormatAttributeDriven(
        IncrementalGeneratorInitializationContext context,
        string metadataName,
        FormatConfig config,
        AttributeHelpers attrs
    ) => ExpandFromAttribute(context, metadataName, config, attrs);

    /// <summary>Pipeline E — types discovered through <c>[PicoDerivedType]</c>.</summary>
    internal static IncrementalValuesProvider<TypeInfo> PolymorphicDriven(
        IncrementalGeneratorInitializationContext context,
        FormatConfig config,
        AttributeHelpers attrs
    ) =>
        context
            .SyntaxProvider.ForAttributeWithMetadataName(
                DerivedTypeAttributeMetadataName,
                predicate: static (node, _) => node is TypeDeclarationSyntax,
                transform: (ctx, _) => GenInfrastructure.ExpandPolymorphicTypes(ctx, config, attrs)
            )
            .SelectMany(static (types, _) => types);

    /// <summary>
    /// Pipeline F — the assembly name, sanitized into something usable inside an identifier. One
    /// provider, reused by the anonymous-type output and (JSON only) by the merged output, so the two
    /// cannot disagree about the prefix they are part of.
    /// </summary>
    internal static IncrementalValueProvider<string> AssemblyName(
        IncrementalGeneratorInitializationContext context
    ) =>
        context.CompilationProvider.Select(
            static (c, _) =>
                (c.AssemblyName ?? "unknown").Replace('.', '_').Replace('-', '_').Replace(' ', '_')
        );

    /// <summary>
    /// Pipeline-Anon — the anonymous types passed to this format's serializer, their two capability
    /// diagnostics, and one interceptor class per type. Registered as ONE unit because its providers
    /// exist only to feed one callback: the capability flags come from a `Select`, which has nowhere to
    /// report a diagnostic, so report-and-skip has to live in the callback next to the emission it
    /// guards (review P3-1). A generator without anonymous types simply does not call this.
    /// </summary>
    internal static void RegisterAnonOutput(
        IncrementalGeneratorInitializationContext context,
        AnonPipelineConfig config,
        IncrementalValueProvider<string> assemblyName
    )
    {
        var anonDriven = context
            .SyntaxProvider.CreateSyntaxProvider(
                predicate: static (n, _) => GenInfrastructure.IsCandidate(n),
                transform: (ctx, _) => AnonTransform(ctx, config.Format, config.Attributes)
            )
            .Where(a => a is not null);

        var anonCaps = context.CompilationProvider.Select(
            static (c, _) =>
            {
                var comp = (CSharpCompilation)c;
                return (
                    LangOk: comp.LanguageVersion >= LanguageVersion.CSharp12,
                    Unsafe: comp.Options.AllowUnsafe
                );
            }
        );

        var anonOut = anonDriven.Collect().Combine(assemblyName).Combine(anonCaps);
        context.RegisterSourceOutput(
            anonOut,
            (spc, pair) =>
            {
                var anonTypes = pair.Left.Left;
                var __asmName = pair.Left.Right;
                var caps = pair.Right;
                if (anonTypes.Length > 0 && !caps.LangOk)
                    spc.ReportDiagnostic(Diagnostic.Create(config.RequiresCSharp12, null));
                else if (anonTypes.Length > 0 && !caps.Unsafe)
                    spc.ReportDiagnostic(Diagnostic.Create(config.RequiresUnsafe, null));
                if (anonTypes.Length == 0 || !caps.LangOk || !caps.Unsafe)
                    return;
                GenInfrastructure.AssemblyPrefix = $"__PicoSerDe_{__asmName}";
                foreach (var ai in anonTypes)
                {
                    if (ai is not { } info)
                        continue;
                    AnonTypeHandler.GenerateInterceptorClass(
                        spc,
                        info,
                        config.Format,
                        config.AnonFormat,
                        config.EmitField
                    );
                }
            }
        );
    }

    /// <summary>
    /// The merge the five type sources feed: usage-driven first, then attribute, format attribute,
    /// shorthand and polymorphic, each appended in that order. Order and intermediate shapes are
    /// reproduced on purpose — see the note at the top about the graph being the cache granularity.
    /// </summary>
    internal static IncrementalValueProvider<ImmutableArray<TypeInfo>> Merge(
        IncrementalValuesProvider<TypeInfo> usageDriven,
        IncrementalValuesProvider<TypeInfo> attributeDriven,
        IncrementalValuesProvider<TypeInfo> formatAttributeDriven,
        IncrementalValuesProvider<TypeInfo> shorthandAttributeDriven,
        IncrementalValuesProvider<TypeInfo> polymorphicDriven
    ) =>
        usageDriven
            .Collect()
            .Combine(attributeDriven.Collect())
            .Select(static (pair, _) => pair.Left.AddRange(pair.Right))
            .Combine(formatAttributeDriven.Collect())
            .Select(static (pair, _) => pair.Left.AddRange(pair.Right))
            .Combine(shorthandAttributeDriven.Collect())
            .Select(static (pair, _) => pair.Left.AddRange(pair.Right))
            .Combine(polymorphicDriven.Collect())
            .Select(static (pair, _) => pair.Left.AddRange(pair.Right));

    /// <summary>
    /// Registers the merged output with NO assembly prefix — the four formats whose helpers live in
    /// the type's own namespace.
    /// </summary>
    internal static void RegisterTypesOutput(
        IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<ImmutableArray<TypeInfo>> types,
        Action<SourceProductionContext, ImmutableArray<TypeInfo>> emitAll
    ) =>
        context.RegisterSourceOutput(
            types,
            (spc, value) =>
            {
                GenInfrastructure.AssemblyPrefix = null;
                emitAll(spc, value);
            }
        );

    /// <summary>
    /// Registers the merged output SCOPED TO THE ASSEMBLY — only the JSON generator, whose helper
    /// types live under <c>__PicoSerDe_&lt;asm&gt;</c> to avoid CS0436 when two assemblies reference the
    /// same shape. A separate method, not a flag: the graph really differs (the merged output depends
    /// on the compilation here and does not in <see cref="RegisterTypesOutput"/>).
    /// </summary>
    internal static void RegisterAssemblyScopedTypesOutput(
        IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<ImmutableArray<TypeInfo>> types,
        IncrementalValueProvider<string> assemblyName,
        Action<SourceProductionContext, ImmutableArray<TypeInfo>> emitAll
    ) =>
        context.RegisterSourceOutput(
            types.Combine(assemblyName),
            (spc, pair) =>
            {
                GenInfrastructure.AssemblyPrefix = $"__PicoSerDe_{pair.Right}";
                emitAll(spc, pair.Left);
            }
        );

    /// <summary>
    /// The three attribute-driven sources differ only in which metadata name they listen for, so they
    /// share one body: same type-declaration predicate, same expansion, same flattening.
    /// </summary>
    private static IncrementalValuesProvider<TypeInfo> ExpandFromAttribute(
        IncrementalGeneratorInitializationContext context,
        string metadataName,
        FormatConfig config,
        AttributeHelpers attrs
    ) =>
        context
            .SyntaxProvider.ForAttributeWithMetadataName(
                metadataName,
                predicate: static (node, _) => node is TypeDeclarationSyntax,
                transform: (ctx, _) => GenInfrastructure.ExpandAttributes(ctx, config, attrs)
            )
            .SelectMany(static (types, _) => types);

    /// <summary>
    /// The anonymous-type extraction itself: a single-type-argument call on the format's own
    /// <c>Serialize</c>, whose argument is an anonymous type. Null for every other candidate.
    /// </summary>
    private static AnonTypeInfo? AnonTransform(
        GeneratorSyntaxContext ctx,
        FormatConfig config,
        AttributeHelpers attrs
    )
    {
        if (ctx.SemanticModel.GetSymbolInfo(ctx.Node).Symbol is not IMethodSymbol m)
            return null;
        if (m.TypeArguments.Length != 1)
            return null;
        if (m.TypeArguments[0] is not INamedTypeSymbol nt || !nt.IsAnonymousType)
            return null;
        if (
            m.ContainingType.Name != config.SerializerClassName
            || m.ContainingType.ContainingNamespace?.ToDisplayString() != config.Namespace
        )
            return null;
        return AnonTypeHandler.BuildAnonTypeInfo(nt, ctx, config, attrs);
    }
}
