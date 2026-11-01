// The five generators each read their own attributes off a member or a type, and each of them
// spelled that read out by hand: **twenty-eight** `foreach (var attr in symbol.GetAttributes())`
// bodies — **twenty-three** of them across the four non-MessagePack `.Gen` projects (INI 8, JSON 5,
// TOML 5, YAML 5) plus **five** inline copies in the MessagePack generator — every one the same loop
// around a different attribute NAME. A change to "how is an attribute matched" was five edits in five
// slightly different shapes, which is how the `== 1` / `>= 1` split below came to exist unnoticed in
// the first place. (Earlier prose here and in `AGENTS.md` said "twenty-three … plus five" while
// calling the four contributors "five generators", and the C3 merge subject said "twenty-seven";
// the numbers in this paragraph are the ones the tree actually shows.)
//
// THE FIRST CUT WAS ASYMMETRIC, AND THAT IS WHY THIS PARAGRAPH EXISTS. The commit that created this
// file left five further loops behind of the very shape it exists to remove — the four
// `[IniConstructor]` / `[JsonConstructor]` / `[TomlConstructor]` / `[YamlConstructor]` scans and the
// `[YamlTag]` scan — plus a sixth copy inside `GenInfrastructure.DetectConstructor`. A partially
// converged tree is worse than an unconverged one: it reads as "this is already unified" while the
// next matcher change still has six places to touch. Those six were folded in afterwards, which puts
// **thirty-four** sites behind this file (28 + 5 + 1). The only `foreach (... GetAttributes())`
// bodies left in the tree are the three in `GenInfrastructure` that read an attribute's PAYLOAD
// rather than asking whether it is present — `[PicoIgnore]`'s condition,
// `[PicoSerializable(IncludeFields = true)]`, and the `[PicoPolymorphic]` / `[PicoDerivedType]`
// pair — and reading a named argument is not a lookup this file answers.
//
// WHAT IS PARAMETRIC. Everything a format legitimately differs on is an argument: the attribute's
// class name, the namespace it has to be declared in, how many constructor arguments it must carry,
// and — TOML only — whether the generic form `[TomlConverter<T>]` also counts. That is the same rule
// FusedEmit follows for the reader's local name and type: the shared code owns the shape, the caller
// owns the literals.
//
// WHAT IS NOT HERE, ON PURPOSE. Which attributes a format has, and which slots of its
// `AttributeHelpers` record it fills, stays in each generator: this file answers "does THIS attribute
// carry a name, and what is it", never "should this format honour camelCase". Sharing the decision
// as well as the loop is what makes a format that legitimately differs unrepresentable.
//
// THE NAMESPACE ARGUMENT IS NULLABLE, AND NULL MEANS "NO FILTER". Twenty-four sites require the
// attribute to live in a named namespace; the other ten never checked one, so they pass null and get
// exactly the behaviour they had. In particular the four `[XxxConstructor]` scans pass null: their hand-written
// bodies compared the attribute NAME only, and "tidying" them into a `PicoIni`/`PicoJetson`/… filter
// would be a silent behaviour change. There is deliberately no overload without the argument — a
// silently skipped namespace check is how an attribute belonging to another format would start being
// honoured.

namespace PicoSerDe.Gen;

/// <summary>
/// How many constructor arguments an attribute has to carry before its first one is read.
/// <para>
/// Two members because the hand-written bodies genuinely disagreed, and collapsing the
/// difference would have been a silent behaviour change for a multi-argument attribute: the strict
/// form (<c>ConstructorArguments.Length == 1</c>) refuses an attribute it does not fully understand,
/// the loose form (<c>&gt;= 1</c>) reads the first argument and ignores the rest. Every format's
/// attribute declares a single-argument constructor today, so the two agree on all real input — the
/// distinction is kept because the alternative is a decision this refactor was not asked to make.
/// </para>
/// </summary>
internal enum AttributeArity
{
    /// <summary>The strict form: the attribute must have exactly one constructor argument.</summary>
    One,

    /// <summary>The loose form: one argument or more; only the first is read.</summary>
    AtLeastOne,
}

/// <summary>
/// The one body behind every generated-code attribute read: find the attribute by name (and, when
/// asked, by declaring namespace) and return the value carried by its first constructor argument.
/// <para>
/// Every method takes an <see cref="ISymbol"/> because the callers pass either an
/// <see cref="IPropertySymbol"/> or an <see cref="ITypeSymbol"/> and <c>GetAttributes</c> lives on the
/// common base: the two used to be separate overloads with identical bodies, which is precisely the
/// duplication this file removes.
/// </para>
/// </summary>
internal static class AttributeLookup
{
    /// <summary>
    /// True when <paramref name="symbol"/> carries <paramref name="attributeName"/> (declared in
    /// <paramref name="inNamespace"/> unless that is null). The argument count is not inspected: a
    /// flag attribute takes none.
    /// </summary>
    internal static bool Has(ISymbol symbol, string? inNamespace, string attributeName)
    {
        foreach (var attribute in symbol.GetAttributes())
            if (Matches(attribute, inNamespace, attributeName))
                return true;

        return false;
    }

    /// <summary>
    /// The first constructor argument of the matching attribute when it is a string, else null: the
    /// custom key, the section name, the date format, the comment text.
    /// </summary>
    internal static string? GetString(
        ISymbol symbol,
        string? inNamespace,
        string attributeName,
        AttributeArity arity
    )
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            if (!Matches(attribute, inNamespace, attributeName))
                continue;
            if (
                HasArgument(attribute, arity)
                && attribute.ConstructorArguments[0].Value is string value
            )
                return value;
        }

        return null;
    }

    /// <summary>
    /// The fully-qualified name of the converter type named by the matching attribute, else null.
    /// <para>
    /// <paramref name="genericTypeArgumentFallback"/> is TOML's one genuine difference: its converter
    /// attribute also exists in a generic form, where the converter is the class's type argument rather
    /// than a constructor argument. The fallback is checked only after the constructor argument has
    /// been ruled out, which is the order the hand-written TOML body used.
    /// </para>
    /// </summary>
    internal static string? GetTypeName(
        ISymbol symbol,
        string? inNamespace,
        string attributeName,
        AttributeArity arity,
        bool genericTypeArgumentFallback = false
    )
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            if (!Matches(attribute, inNamespace, attributeName))
                continue;
            if (
                HasArgument(attribute, arity)
                && attribute.ConstructorArguments[0].Value is INamedTypeSymbol converterType
            )
                return converterType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (genericTypeArgumentFallback && attribute.AttributeClass?.TypeArguments.Length == 1)
                return attribute
                    .AttributeClass.TypeArguments[0]
                    .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }

        return null;
    }

    /// <summary>
    /// The first constructor argument of the matching attribute when it is a <typeparamref name="T"/>,
    /// else null: MessagePack's integer key and its extension tag, the only two attributes that carry
    /// a value instead of a name.
    /// </summary>
    internal static T? GetValue<T>(
        ISymbol symbol,
        string? inNamespace,
        string attributeName,
        AttributeArity arity
    )
        where T : struct
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            if (!Matches(attribute, inNamespace, attributeName))
                continue;
            if (HasArgument(attribute, arity) && attribute.ConstructorArguments[0].Value is T value)
                return value;
        }

        return null;
    }

    /// <summary>
    /// Does this attribute answer to the caller's name? A null <paramref name="inNamespace"/> accepts
    /// any declaring namespace (MessagePack), a non-null one requires an exact match.
    /// </summary>
    private static bool Matches(
        AttributeData attribute,
        string? inNamespace,
        string attributeName
    ) =>
        attribute.AttributeClass?.Name == attributeName
        && (
            inNamespace is null
            || attribute.AttributeClass.ContainingNamespace?.ToDisplayString() == inNamespace
        );

    /// <summary>The <see cref="AttributeArity"/> check, in one place so the two forms cannot drift.</summary>
    private static bool HasArgument(AttributeData attribute, AttributeArity arity) =>
        arity == AttributeArity.One
            ? attribute.ConstructorArguments.Length == 1
            : attribute.ConstructorArguments.Length >= 1;
}
