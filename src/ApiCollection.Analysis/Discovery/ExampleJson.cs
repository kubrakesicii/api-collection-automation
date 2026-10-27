using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static ApiCollection.Analysis.Discovery.SyntaxHelpers;

namespace ApiCollection.Analysis.Discovery;

internal static class ExampleJson
{
    public static readonly HashSet<string> IntegerTypes =
        ["byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong", "Byte", "SByte", "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64"];

    public static readonly HashSet<string> FloatingTypes = ["float", "double", "decimal", "Single", "Double", "Decimal"];

    public static readonly HashSet<string> BooleanTypes = ["bool", "Boolean"];

    public static readonly HashSet<string> StringTypes =
        ["string", "String", "char", "Char", "Guid", "DateTime", "DateTimeOffset", "DateOnly", "TimeOnly", "TimeSpan", "Uri"];

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string Pretty(JsonNode? node) => node is null ? "null" : node.ToJsonString(Indented);

    public static string Compact(JsonNode? node) => node is null ? "null" : node.ToJsonString();

    /// <summary>Parses JSON text; a node can only have one parent, so always parse a fresh copy before reuse.</summary>
    public static bool TryParse(string json, out JsonNode? node)
    {
        try
        {
            node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true });
            return true;
        }
        catch (JsonException)
        {
            node = null;
            return false;
        }
    }

    /// <summary>Converts a string, number, boolean, char or null literal to JSON (null literal yields a null node).</summary>
    public static bool TryLiteralNode(ExpressionSyntax expression, out JsonNode? node)
    {
        node = null;
        switch (expression)
        {
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression):
                node = JsonValue.Create(literal.Token.ValueText);
                return true;
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.CharacterLiteralExpression):
                node = JsonValue.Create(literal.Token.ValueText);
                return true;
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.TrueLiteralExpression):
                node = JsonValue.Create(true);
                return true;
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.FalseLiteralExpression):
                node = JsonValue.Create(false);
                return true;
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.NullLiteralExpression):
                return true;
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.NumericLiteralExpression):
                node = Number(literal.Token.Value, negate: false);
                return node is not null;
            case PrefixUnaryExpressionSyntax unary when unary.IsKind(SyntaxKind.UnaryMinusExpression)
                && unary.Operand is LiteralExpressionSyntax inner && inner.IsKind(SyntaxKind.NumericLiteralExpression):
                node = Number(inner.Token.Value, negate: true);
                return node is not null;
            case ParenthesizedExpressionSyntax parenthesized:
                return TryLiteralNode(parenthesized.Expression, out node);
            default:
                return false;
        }
    }

    private static JsonNode? Number(object? value, bool negate) => value switch
    {
        int i => JsonValue.Create(negate ? -i : i),
        long l => JsonValue.Create(negate ? -l : l),
        uint u => negate ? JsonValue.Create(-(long)u) : JsonValue.Create(u),
        ulong ul => negate ? null : JsonValue.Create(ul),
        float f => JsonValue.Create(negate ? -f : f),
        double d => JsonValue.Create(negate ? -d : d),
        decimal m => JsonValue.Create(negate ? -m : m),
        _ => null
    };

    /// <summary>Converts the text of an XML &lt;example&gt; element into JSON for the given property type; null if impossible.</summary>
    public static JsonNode? FromDocumentationText(string text, TypeSyntax type)
    {
        var baseType = StripNullable(type);
        var name = LastIdentifier(baseType);

        if (IsCollection(baseType) || text.StartsWith('{') || text.StartsWith('['))
        {
            return TryParse(text, out var parsed) ? parsed : null;
        }

        if (BooleanTypes.Contains(name))
        {
            return bool.TryParse(text, out var flag) ? JsonValue.Create(flag) : null;
        }

        if (IntegerTypes.Contains(name))
        {
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) ? JsonValue.Create(integer) : null;
        }

        if (FloatingTypes.Contains(name))
        {
            return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? JsonValue.Create(number) : null;
        }

        return JsonValue.Create(text);
    }
}
