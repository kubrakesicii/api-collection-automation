using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ApiCollection.Analysis.Discovery;

internal static class SyntaxHelpers
{
    private static readonly HashSet<string> CollectionNames =
        ["List", "IList", "IEnumerable", "IReadOnlyList", "ICollection", "IReadOnlyCollection", "HashSet", "ISet"];

    public static IEnumerable<AttributeSyntax> Attributes(MemberDeclarationSyntax member) =>
        member.AttributeLists.SelectMany(l => l.Attributes);

    public static string AttributeName(AttributeSyntax attribute)
    {
        var name = LastIdentifier(attribute.Name);
        return name.EndsWith("Attribute", StringComparison.Ordinal) && name.Length > "Attribute".Length
            ? name[..^"Attribute".Length]
            : name;
    }

    public static string LastIdentifier(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax qualified => LastIdentifier(qualified.Right),
        AliasQualifiedNameSyntax alias => alias.Name.Identifier.Text,
        SimpleNameSyntax simple => simple.Identifier.Text,
        _ => type.ToString()
    };

    public static ExpressionSyntax? TemplateArgument(AttributeSyntax attribute)
    {
        foreach (var argument in attribute.ArgumentList?.Arguments ?? [])
        {
            if (argument.NameEquals is null && argument.NameColon is null)
            {
                return argument.Expression;
            }

            if (argument.NameEquals?.Name.Identifier.Text == "Template" || argument.NameColon?.Name.Identifier.Text == "template")
            {
                return argument.Expression;
            }
        }

        return null;
    }

    /// <summary>Evaluates a string literal, or a concatenation of string literals.</summary>
    public static bool TryEvaluate(ExpressionSyntax expression, out string value)
    {
        switch (expression)
        {
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression):
                value = literal.Token.ValueText;
                return true;
            case ParenthesizedExpressionSyntax parenthesized:
                return TryEvaluate(parenthesized.Expression, out value);
            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression)
                && TryEvaluate(binary.Left, out var left) && TryEvaluate(binary.Right, out var right):
                value = left + right;
                return true;
            default:
                value = string.Empty;
                return false;
        }
    }

    /// <summary>Evaluates a string, numeric, boolean or null literal (including negative numbers).</summary>
    public static bool TryLiteral(ExpressionSyntax expression, out string value)
    {
        switch (expression)
        {
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression):
                value = literal.Token.ValueText;
                return true;
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.NumericLiteralExpression):
                value = Convert.ToString(literal.Token.Value, CultureInfo.InvariantCulture) ?? literal.Token.Text;
                return true;
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.TrueLiteralExpression):
                value = "true";
                return true;
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.FalseLiteralExpression):
                value = "false";
                return true;
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.NullLiteralExpression):
                value = "null";
                return true;
            case PrefixUnaryExpressionSyntax unary when unary.IsKind(SyntaxKind.UnaryMinusExpression)
                && unary.Operand is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } number:
                value = "-" + Convert.ToString(number.Token.Value, CultureInfo.InvariantCulture);
                return true;
            case ParenthesizedExpressionSyntax parenthesized:
                return TryLiteral(parenthesized.Expression, out value);
            default:
                value = string.Empty;
                return false;
        }
    }

    /// <summary>Unwraps nullable, array and common collection types down to the element type.</summary>
    public static TypeSyntax ElementType(TypeSyntax type)
    {
        while (true)
        {
            switch (type)
            {
                case NullableTypeSyntax nullable:
                    type = nullable.ElementType;
                    continue;
                case ArrayTypeSyntax array:
                    type = array.ElementType;
                    continue;
                case QualifiedNameSyntax qualified:
                    type = qualified.Right;
                    continue;
                case GenericNameSyntax generic when generic.TypeArgumentList.Arguments.Count == 1
                    && (generic.Identifier.Text == "Nullable" || CollectionNames.Contains(generic.Identifier.Text)):
                    type = generic.TypeArgumentList.Arguments[0];
                    continue;
                default:
                    return type;
            }
        }
    }

    public static TypeSyntax StripNullable(TypeSyntax type) => type switch
    {
        NullableTypeSyntax nullable => nullable.ElementType,
        GenericNameSyntax { Identifier.Text: "Nullable", TypeArgumentList.Arguments.Count: 1 } generic => generic.TypeArgumentList.Arguments[0],
        _ => type
    };

    public static bool IsNullableType(TypeSyntax type) =>
        type is NullableTypeSyntax or GenericNameSyntax { Identifier.Text: "Nullable" };
}
