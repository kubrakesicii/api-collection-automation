using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static ApiCollection.Analysis.Discovery.SyntaxHelpers;

namespace ApiCollection.Analysis.Discovery;

/// <summary>Index of the type declarations found in the analyzed source, looked up by simple name.</summary>
internal sealed class SourceCatalog
{
    private static readonly HashSet<string> SimpleNames =
    [
        "String", "Boolean", "Byte", "SByte", "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64",
        "Single", "Double", "Decimal", "Char", "Guid", "DateTime", "DateTimeOffset", "DateOnly", "TimeOnly",
        "TimeSpan", "Uri", "Version"
    ];

    private static readonly HashSet<string> OtherLeafNames =
    [
        "Object", "Dictionary", "IDictionary", "IReadOnlyDictionary", "JsonElement", "JsonNode", "JsonObject",
        "JsonArray", "JsonDocument", "IFormFile", "IFormFileCollection"
    ];

    private readonly List<TypeDeclarationSyntax> _types;
    private readonly Dictionary<string, List<string>> _enums = [];

    public SourceCatalog(IReadOnlyList<SyntaxNode> roots)
    {
        _types = roots.SelectMany(r => r.DescendantNodes().OfType<TypeDeclarationSyntax>()).ToList();
        foreach (var declaration in roots.SelectMany(r => r.DescendantNodes().OfType<EnumDeclarationSyntax>()))
        {
            if (!_enums.TryGetValue(declaration.Identifier.Text, out var members))
            {
                _enums[declaration.Identifier.Text] = members = [];
            }

            members.AddRange(declaration.Members.Select(m => m.Identifier.Text));
        }
    }

    public IReadOnlyList<string> EnumMembers(string name) => _enums.GetValueOrDefault(name) ?? [];

    public IReadOnlyList<TypeDeclarationSyntax> Types => _types;

    /// <summary>All (partial) declarations of a class, struct or record with the given simple name.</summary>
    public List<TypeDeclarationSyntax> FindTypes(string name) =>
        _types.Where(t => t.Identifier.Text == name && t is not InterfaceDeclarationSyntax).ToList();

    public bool IsEnum(string name) => _enums.ContainsKey(name);

    /// <summary>True for primitives, common BCL value-like types and enums declared in the source.</summary>
    public bool IsSimpleType(TypeSyntax element) =>
        element is PredefinedTypeSyntax predefined
            ? !predefined.Keyword.IsKind(SyntaxKind.ObjectKeyword)
            : SimpleNames.Contains(LastIdentifier(element)) || IsEnum(LastIdentifier(element));

    /// <summary>True when the type needs no property expansion.</summary>
    public bool IsLeafType(TypeSyntax element) =>
        IsSimpleType(element) || element is PredefinedTypeSyntax || OtherLeafNames.Contains(LastIdentifier(element));

    public bool IsComplexType(TypeSyntax element) =>
        !IsSimpleType(element) && FindTypes(LastIdentifier(element)).Count > 0;
}
