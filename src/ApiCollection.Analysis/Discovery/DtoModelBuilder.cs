using System.Text.Json.Nodes;
using ApiCollection.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static ApiCollection.Analysis.Discovery.SyntaxHelpers;

namespace ApiCollection.Analysis.Discovery;

/// <summary>
/// Builds property trees for DTO classes, structs and records declared in the analyzed source,
/// including JSON names and the static example/default values found next to each property.
/// </summary>
internal sealed class DtoModelBuilder(SourceCatalog catalog, List<ExampleDiagnostic> exampleDiagnostics)
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
        var typeName = declarations[0].Identifier.Text;

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

                var attributes = parameter.AttributeLists
                    .Where(l => l.Target is null || l.Target.Identifier.IsKind(SyntaxKind.PropertyKeyword))
                    .SelectMany(l => l.Attributes)
                    .ToList();
                var name = parameter.Identifier.Text;
                Add(properties, new PropertyInfo(
                    name, parameter.Type, attributes.Any(a => AttributeName(a) == "Required"),
                    JsonName(attributes),
                    DefaultValueAttribute(attributes, typeName, name),
                    Literal(parameter.Default?.Value, ExampleSource.ConstructorDefault, typeName, name)),
                    owner, warnings, stack);
            }

            foreach (var property in declaration.Members.OfType<PropertyDeclarationSyntax>())
            {
                if (!IsSerializedProperty(property))
                {
                    continue;
                }

                var attributes = Attributes(property).ToList();
                var name = property.Identifier.Text;
                var required = property.Modifiers.Any(SyntaxKind.RequiredKeyword) || attributes.Any(a => AttributeName(a) == "Required");
                Add(properties, new PropertyInfo(
                    name, property.Type, required, JsonName(attributes),
                    XmlExample(property, typeName, name, property.Type) ?? DefaultValueAttribute(attributes, typeName, name),
                    Literal(property.Initializer?.Value, ExampleSource.PropertyInitializer, typeName, name)),
                    owner, warnings, stack);
            }
        }

        return properties;
    }

    private sealed record PropertyInfo(
        string Name, TypeSyntax Type, bool Required, string? JsonName, ExampleValue? Example, ExampleValue? Default);

    private static bool IsSerializedProperty(PropertyDeclarationSyntax property) =>
        property.Modifiers.Any(SyntaxKind.PublicKeyword)
        && !property.Modifiers.Any(SyntaxKind.StaticKeyword)
        && (property.AccessorList is null || property.AccessorList.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)))
        && !Attributes(property).Any(a => AttributeName(a) == "JsonIgnore");

    private void Add(List<PropertyDefinition> properties, PropertyInfo info, string owner, List<string> warnings, List<string> stack)
    {
        properties.RemoveAll(p => p.Name == info.Name);
        properties.Add(new PropertyDefinition
        {
            Name = info.Name,
            TypeName = info.Type.ToString(),
            IsRequired = info.Required,
            IsNullable = IsNullableType(info.Type),
            Properties = BuildForType(info.Type, owner, warnings, stack),
            JsonName = info.JsonName,
            Example = info.Example,
            DefaultValue = info.Default
        });
    }

    // ---- example and naming sources ----

    private static string? JsonName(IEnumerable<AttributeSyntax> attributes)
    {
        var attribute = attributes.FirstOrDefault(a => AttributeName(a) == "JsonPropertyName");
        var argument = attribute?.ArgumentList?.Arguments.FirstOrDefault();
        return argument is not null && TryEvaluate(argument.Expression, out var name) ? name : null;
    }

    /// <summary>A literal usable as an example; empty strings and null are placeholders, not examples.</summary>
    private static ExampleValue? Literal(ExpressionSyntax? expression, ExampleSource source, string typeName, string propertyName)
    {
        if (expression is null || !ExampleJson.TryLiteralNode(expression, out var node)
            || node is null || (node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length == 0))
        {
            return null;
        }

        return new ExampleValue
        {
            Json = ExampleJson.Compact(node),
            Source = source,
            SourceDetail = $"{Describe(expression)}: {typeName}.{propertyName}"
        };
    }

    private static ExampleValue? DefaultValueAttribute(IEnumerable<AttributeSyntax> attributes, string typeName, string propertyName)
    {
        var attribute = attributes.FirstOrDefault(a => AttributeName(a) == "DefaultValue");
        var argument = attribute?.ArgumentList?.Arguments.FirstOrDefault();
        return attribute is null ? null : Literal(argument?.Expression, ExampleSource.DefaultValueAttribute, typeName, propertyName);
    }

    private ExampleValue? XmlExample(MemberDeclarationSyntax member, string typeName, string propertyName, TypeSyntax type)
    {
        foreach (var trivia in member.GetLeadingTrivia())
        {
            if (trivia.GetStructure() is not DocumentationCommentTriviaSyntax documentation)
            {
                continue;
            }

            var element = documentation.DescendantNodes().OfType<XmlElementSyntax>()
                .FirstOrDefault(e => e.StartTag.Name.ToString() == "example");
            if (element is null)
            {
                continue;
            }

            var lines = string.Concat(element.Content.OfType<XmlTextSyntax>().SelectMany(t => t.TextTokens).Select(t => t.ValueText))
                .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var text = string.Join(' ', lines);

            var node = text.Length == 0 ? null : ExampleJson.FromDocumentationText(text, type);
            if (node is null)
            {
                exampleDiagnostics.Add(new ExampleDiagnostic(
                    "EX003", null, $"{typeName}.{propertyName}",
                    $"XML <example> value '{text}' ({Describe(element)}) cannot be converted to type '{type}'; it was ignored."));
                return null;
            }

            return new ExampleValue
            {
                Json = ExampleJson.Compact(node),
                Source = ExampleSource.XmlDocExample,
                SourceDetail = $"{Describe(element)}: {typeName}.{propertyName}"
            };
        }

        return null;
    }
}
