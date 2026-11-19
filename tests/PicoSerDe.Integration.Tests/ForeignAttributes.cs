// The stand-in symbol for A9 (see `AttributePipelineFencesTests.cs`, section A9).
//
// `shared/Gen/AttributeLookup.cs` matches an attribute by CLASS NAME **and** by the namespace it
// is declared in (`Matches`, the `inNamespace` argument). The second half had no assertion: a
// same-named attribute from anywhere else was silently as good as the real one. Proving the
// filter needs an attribute the compiler accepts on a property but that the generator must
// refuse, which is what this is — same class name as `PicoToml.TomlKeyAttribute`, different
// declaring namespace, and no PicoSerDe attribute base class to inherit.
//
// Deliberately NOT in a namespace a test file also uses: the lookup compares the DECLARING
// namespace exactly, so "somewhere else" has to be somewhere the generator never names.

namespace ForeignAttributes;

/// <summary>
/// Not a PicoSerDe attribute. Only a lookup that forgot its namespace filter can see it.
/// </summary>
[System.AttributeUsage(System.AttributeTargets.Property)]
public sealed class TomlKeyAttribute : System.Attribute
{
    public TomlKeyAttribute(string key) => Key = key;

    public string Key { get; }
}
