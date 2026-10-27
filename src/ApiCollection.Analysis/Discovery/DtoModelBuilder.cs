using ApiCollection.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static ApiCollection.Analysis.Discovery.SyntaxHelpers;

namespace ApiCollection.Analysis.Discovery;

/// <summary>Builds property trees for DTO classes, structs and records declared in the analyzed source.</summary>
internal sealed class DtoModelBuilder(SourceCatalog catalog)
{
    private const int MaxDepth = 16;

    public List<PropertyDefinition> BuildForType(TypeSyntax type, string owner, List<string> warnings) =>
        BuildForType(type, owner, warnings, []);

    private List<PropertyDefinition> BuildForType(TypeSyntax type, string owner, List<string> warnings, List<string> stack)
    {
        var element = ElementType(type);
        if (catalog.IsLeafType(element))
        {
            return [];
        }

        var name = LastIdentifier(element);
        var declarations = catalog.FindTypes(name);
        if (declarations.Count == 0)
        {
            warnings.Add($"{owner}: type '{name}' was not found in the analyzed source; its properties were not extracted.");
            return [];
        }

        if (declarations.Any(d => d.TypeParameterList is not null))
        {
            warnings.Add($"{owner}: generic type '{name}' is not supported; its properties were not extracted.");
            return [];
        }

        if (stack.Contains(name) || stack.Count >= MaxDepth)
        {
            warnings.Add($"{owner}: type '{name}' is recursive or too deeply nested; nested properties were not extracted.");
            return [];
        }

        stack.Add(name);
        try
        {
            return BuildProperties(declarations, owner, warnings, stack);
        }
        finally
        {
            stack.RemoveAt(stack.Count - 1);
        }
    }

    private List<PropertyDefinition> BuildProperties(
        List<TypeDeclarationSyntax> declarations, string owner, List<string> warnings, List<string> stack)
    {
        var properties = new List<PropertyDefinition>();

        // Inherited properties come first; a redeclared property replaces the base one.
        var baseName = declarations
            .SelectMany(d => d.BaseList?.Types ?? [])
            .Select(t => LastIdentifier(t.Type))
            .FirstOrDefault(n => catalog.FindTypes(n).Count > 0);
        if (baseName is not null && !stack.Contains(baseName))
        {
            stack.Add(baseName);
            properties.AddRange(BuildProperties(catalog.FindTypes(baseName), owner, warnings, stack));
            stack.RemoveAt(stack.Count - 1);
        }

        foreach (var declaration in declarations)
        {
            foreach (var parameter in (declaration as RecordDeclarationSyntax)?.ParameterList?.Parameters ?? [])
            {
                if (parameter.Type is null)
                {
                    continue;
                }

                var required = parameter.AttributeLists
                    .Where(l => l.Target is null || l.Target.Identifier.IsKind(SyntaxKind.PropertyKeyword))
                    .SelectMany(l => l.Attributes)
                    .Any(a => AttributeName(a) == "Required");
                Add(properties, parameter.Identifier.Text, parameter.Type, required, owner, warnings, stack);
            }

            foreach (var property in declaration.Members.OfType<PropertyDeclarationSyntax>())
            {
                if (!IsSerializedProperty(property))
                {
                    continue;
                }

                var required = property.Modifiers.Any(SyntaxKind.RequiredKeyword)
                    || Attributes(property).Any(a => AttributeName(a) == "Required");
                Add(properties, property.Identifier.Text, property.Type, required, owner, warnings, stack);
            }
        }

        return properties;
    }

    private static bool IsSerializedProperty(PropertyDeclarationSyntax property) =>
        property.Modifiers.Any(SyntaxKind.PublicKeyword)
        && !property.Modifiers.Any(SyntaxKind.StaticKeyword)
        && (property.AccessorList is null || property.AccessorList.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)))
        && !Attributes(property).Any(a => AttributeName(a) == "JsonIgnore");

    private void Add(
        List<PropertyDefinition> properties, string name, TypeSyntax type, bool required,
        string owner, List<string> warnings, List<string> stack)
    {
        properties.RemoveAll(p => p.Name == name);
        properties.Add(new PropertyDefinition
        {
            Name = name,
            TypeName = type.ToString(),
            IsRequired = required,
            IsNullable = IsNullableType(type),
            Properties = BuildForType(type, owner, warnings, stack)
        });
    }
}
