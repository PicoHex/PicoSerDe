namespace System.Runtime.CompilerServices;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class InterceptsLocationAttribute : Attribute
{
    public InterceptsLocationAttribute(string filePath, int line, int column) { }
}
