using System.Text;

namespace PicoJetson;

/// <summary>Controls when properties are skipped during serialization.</summary>
public enum JsonIgnoreCondition
{
    /// <summary>Always serialize the property.</summary>
    Never = 0,
    /// <summary>Skip properties whose value is null.</summary>
    WhenWritingNull = 1,
    /// <summary>Skip properties whose value is null or the type's default.</summary>
    WhenWritingDefault = 2,
}

/// <summary>Controls how comments in JSON are handled during deserialization.</summary>
public enum JsonCommentHandling
{
    /// <summary>Disallow comments; throw on encountering them.</summary>
    Disallow = 0,
    /// <summary>Skip comments without throwing.</summary>
    Skip = 1,
}

/// <summary>Controls NaN/Infinity handling during serialization/deserialization.</summary>
public enum JsonNumberHandling
{
    /// <summary>Strict JSON: NaN/Infinity throw during serialization and deserialization.</summary>
    Strict = 0,
    /// <summary>Allow quoted NaN/Infinity/-Infinity literals on deserialization.</summary>
    AllowNamedFloatingPointLiterals = 1,
}

/// <summary>Controls how unmapped properties are handled during deserialization.</summary>
public enum JsonUnmappedMemberHandling
{
    /// <summary>Skip unknown properties silently.</summary>
    Skip = 0,
    /// <summary>Throw on encountering an unknown property.</summary>
    Disallow = 1,
}

/// <summary>Base class for property naming policies.</summary>
public abstract class JsonNamingPolicy
{
    /// <summary>Converts a property name to the target naming convention.</summary>
    public abstract string ConvertName(string name);

    /// <summary>PascalCase preserved as-is.</summary>
    public static JsonNamingPolicy PascalCase { get; } = new PascalCaseNamingPolicy();
    /// <summary>First character lowercased.</summary>
    public static JsonNamingPolicy CamelCase { get; } = new CamelCaseNamingPolicy();
    /// <summary>snake_case.</summary>
    public static JsonNamingPolicy SnakeCaseLower { get; } = new SnakeCaseNamingPolicy();
    /// <summary>kebab-case.</summary>
    public static JsonNamingPolicy KebabCaseLower { get; } = new KebabCaseNamingPolicy();
}

internal sealed class PascalCaseNamingPolicy : JsonNamingPolicy
{
    public override string ConvertName(string name) => name;
}

internal sealed class CamelCaseNamingPolicy : JsonNamingPolicy
{
    public override string ConvertName(string name)
    {
        if (string.IsNullOrEmpty(name) || char.IsLower(name[0]))
            return name;
        int upperLen = 0;
        while (upperLen < name.Length && char.IsUpper(name[upperLen]))
            upperLen++;
        if (upperLen <= 1)
            return char.ToLowerInvariant(name[0]) + name[1..];
        if (upperLen < name.Length)
            return name[..(upperLen - 1)].ToLowerInvariant() + name[(upperLen - 1)..];
        return name.ToLowerInvariant();
    }
}

internal sealed class SnakeCaseNamingPolicy : JsonNamingPolicy
{
    public override string ConvertName(string name)
    {
        if (name.Length == 0) return name;
        var sb = new StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]))
            {
                if (i > 0 && (i + 1 < name.Length && !char.IsUpper(name[i + 1]) || i > 0))
                    sb.Append('_');
                sb.Append(char.ToLowerInvariant(name[i]));
            }
            else
                sb.Append(name[i]);
        }
        return sb.ToString();
    }
}

internal sealed class KebabCaseNamingPolicy : JsonNamingPolicy
{
    public override string ConvertName(string name)
    {
        if (name.Length == 0) return name;
        var sb = new StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]))
            {
                if (i > 0 && (i + 1 < name.Length && !char.IsUpper(name[i + 1]) || i > 0))
                    sb.Append('-');
                sb.Append(char.ToLowerInvariant(name[i]));
            }
            else
                sb.Append(name[i]);
        }
        return sb.ToString();
    }
}

/// <summary>
/// Options for <see cref="JsonSerializer"/>.
/// Default mode is compact (no indentation) — optimal for data transfer.
/// </summary>
public class JsonOptions
{
    // ── Serialization options ──

    /// <summary>Whether to indent the JSON output. Default: false (compact).</summary>
    public bool Indented { get; set; } = false;

    /// <summary>Maximum nesting depth. Default: 63.</summary>
    public int MaxDepth { get; set; } = 63;

    /// <summary>Property naming policy applied during serialization. Default: null (use compiled names).</summary>
    public JsonNamingPolicy? PropertyNamingPolicy { get; set; }

    /// <summary>Controls when properties are skipped based on their value. Default: Never.</summary>
    public JsonIgnoreCondition DefaultIgnoreCondition { get; set; } = JsonIgnoreCondition.Never;

    /// <summary>Whether to serialize public fields in addition to properties. Default: false.</summary>
    public bool IncludeFields { get; set; } = false;

    /// <summary>Controls NaN/Infinity handling. Default: Strict.</summary>
    public JsonNumberHandling NumberHandling { get; set; } = JsonNumberHandling.Strict;

    // ── Deserialization options ──

    /// <summary>Whether property name matching is case-insensitive during deserialization. Default: false.</summary>
    public bool PropertyNameCaseInsensitive { get; set; } = false;

    /// <summary>Whether to allow trailing commas in objects and arrays. Default: false.</summary>
    public bool AllowTrailingCommas { get; set; } = false;

    /// <summary>How comments are handled during deserialization. Default: Disallow.</summary>
    public JsonCommentHandling ReadCommentHandling { get; set; } = JsonCommentHandling.Disallow;

    /// <summary>How unmapped properties are handled during deserialization. Default: Skip.</summary>
    public JsonUnmappedMemberHandling UnmappedMemberHandling { get; set; } = JsonUnmappedMemberHandling.Skip;

    /// <summary>Thread-local current options, used by SG-generated code and reader/writer.</summary>
    /// <remarks>Public so SG-generated code in consumer assemblies can access it.
    /// Set automatically by <c>SerializeToUtf8Bytes</c> and <c>Deserialize</c> overloads.</remarks>
    [ThreadStatic]
    public static JsonOptions? Current;
}
