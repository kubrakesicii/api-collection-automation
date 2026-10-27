using ApiCollection.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static ApiCollection.Analysis.Discovery.SyntaxHelpers;

namespace ApiCollection.Analysis.Discovery;

/// <summary>
/// Extracts statically resolvable FluentValidation rules from <c>AbstractValidator&lt;T&gt;</c> constructors.
/// Anything that cannot be resolved from source (custom predicates, conditions, non-literal arguments) is reported, never guessed.
/// </summary>
internal sealed class ValidationExtractor
{
    private static readonly HashSet<string> KnownRules =
    [
        "NotEmpty", "NotNull", "Empty", "Null", "Length", "MinimumLength", "MaximumLength", "EmailAddress", "Matches",
        "InclusiveBetween", "ExclusiveBetween", "GreaterThan", "GreaterThanOrEqualTo", "LessThan", "LessThanOrEqualTo",
        "Equal", "NotEqual", "CreditCard", "IsInEnum", "PrecisionScale"
    ];

    private static readonly HashSet<string> NonRuleModifiers =
        ["WithMessage", "WithName", "WithErrorCode", "WithSeverity", "WithState", "OverridePropertyName", "Cascade"];

    private static readonly HashSet<string> Conditions = ["When", "Unless", "WhenAsync", "UnlessAsync"];

    private readonly Dictionary<string, List<ClassDeclarationSyntax>> _byTarget = [];
    private readonly Dictionary<string, ClassDeclarationSyntax> _byName = [];

    public ValidationExtractor(SourceCatalog catalog)
    {
        foreach (var validator in catalog.Types.OfType<ClassDeclarationSyntax>())
        {
            var target = ValidatedType(validator);
            if (target is null)
            {
                continue;
            }

            _byName.TryAdd(validator.Identifier.Text, validator);
            if (!_byTarget.TryGetValue(target, out var list))
            {
                _byTarget[target] = list = [];
            }

            list.Add(validator);
        }
    }

    public List<ValidationRuleDefinition> RulesFor(string targetType, List<string> warnings)
    {
        var rules = new List<ValidationRuleDefinition>();
        if (_byTarget.TryGetValue(targetType, out var validators))
        {
            foreach (var validator in validators)
            {
                Collect(validator, string.Empty, [validator.Identifier.Text], rules, warnings);
            }
        }

        return rules;
    }

    private static string? ValidatedType(ClassDeclarationSyntax declaration) =>
        declaration.BaseList?.Types
            .Select(t => t.Type)
            .OfType<GenericNameSyntax>()
            .Where(g => g.Identifier.Text == "AbstractValidator" && g.TypeArgumentList.Arguments.Count == 1)
            .Select(g => LastIdentifier(g.TypeArgumentList.Arguments[0]))
            .FirstOrDefault();

    private void Collect(
        ClassDeclarationSyntax validator, string prefix, HashSet<string> stack,
        List<ValidationRuleDefinition> rules, List<string> warnings)
    {
        var name = validator.Identifier.Text;
        foreach (var constructor in validator.Members.OfType<ConstructorDeclarationSyntax>())
        {
            if (constructor.Body is null)
            {
                continue;
            }

            foreach (var statement in constructor.Body.Statements)
            {
                if (statement is LocalDeclarationStatementSyntax)
                {
                    continue;
                }

                if (statement is ExpressionStatementSyntax { Expression: InvocationExpressionSyntax invocation }
                    && TryParseChain(invocation, out var calls))
                {
                    ProcessChain(name, calls, prefix, stack, rules, warnings);
                }
                else
                {
                    warnings.Add($"{name}: unsupported statement '{Shorten(statement)}' was not analyzed.");
                }
            }
        }
    }

    private void ProcessChain(
        string validatorName, List<InvocationExpressionSyntax> calls, string prefix, HashSet<string> stack,
        List<ValidationRuleDefinition> rules, List<string> warnings)
    {
        var root = calls[0];
        var property = PropertyPath(root);
        if (property is null)
        {
            warnings.Add($"{validatorName}: property expression in '{Shorten(root)}' is not supported; its rules were not extracted.");
            return;
        }

        var path = prefix + property + (MethodName(root) == "RuleForEach" ? "[]" : string.Empty);
        var chain = calls.Skip(1).ToList();

        var condition = chain.Select(MethodName).FirstOrDefault(Conditions.Contains);
        if (condition is not null)
        {
            warnings.Add($"{validatorName}: conditional rule ({condition}) on '{path}' was not extracted.");
            return;
        }

        foreach (var call in chain)
        {
            var ruleName = MethodName(call);
            if (NonRuleModifiers.Contains(ruleName))
            {
                continue;
            }

            if (ruleName == "SetValidator")
            {
                AddNestedValidator(validatorName, call, path, stack, rules, warnings);
                continue;
            }

            if (!KnownRules.Contains(ruleName))
            {
                warnings.Add($"{validatorName}: rule '{ruleName}' on '{path}' cannot be resolved statically and was skipped.");
                continue;
            }

            var arguments = new List<string>();
            var resolvable = true;
            foreach (var argument in call.ArgumentList.Arguments)
            {
                if (TryLiteral(argument.Expression, out var value))
                {
                    arguments.Add(value);
                }
                else
                {
                    resolvable = false;
                    break;
                }
            }

            if (!resolvable)
            {
                warnings.Add($"{validatorName}: rule '{ruleName}' on '{path}' has non-literal arguments and was skipped.");
                continue;
            }

            rules.Add(new ValidationRuleDefinition { PropertyName = path, RuleName = ruleName, Arguments = arguments });
        }
    }

    private void AddNestedValidator(
        string validatorName, InvocationExpressionSyntax call, string path, HashSet<string> stack,
        List<ValidationRuleDefinition> rules, List<string> warnings)
    {
        var nestedName = call.ArgumentList.Arguments.Count == 1
            && call.ArgumentList.Arguments[0].Expression is ObjectCreationExpressionSyntax creation
                ? LastIdentifier(creation.Type)
                : null;

        if (nestedName is null || !_byName.TryGetValue(nestedName, out var nested))
        {
            warnings.Add($"{validatorName}: SetValidator on '{path}' does not create a validator declared in the analyzed source; nested rules were not extracted.");
            return;
        }

        if (!stack.Add(nestedName))
        {
            warnings.Add($"{validatorName}: SetValidator on '{path}' is recursive; nested rules were not extracted.");
            return;
        }

        Collect(nested, path + ".", stack, rules, warnings);
        stack.Remove(nestedName);
    }

    /// <summary>Splits <c>RuleFor(...).A().B()</c> into its calls, root first; false when the statement is not a rule chain.</summary>
    private static bool TryParseChain(InvocationExpressionSyntax statement, out List<InvocationExpressionSyntax> calls)
    {
        calls = [];
        ExpressionSyntax current = statement;
        while (current is InvocationExpressionSyntax invocation)
        {
            calls.Add(invocation);
            if (invocation.Expression is MemberAccessExpressionSyntax member)
            {
                current = member.Expression;
            }
            else
            {
                break;
            }
        }

        calls.Reverse();
        return calls[0].Expression is IdentifierNameSyntax { Identifier.Text: "RuleFor" or "RuleForEach" };
    }

    private static string MethodName(InvocationExpressionSyntax call) => call.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
        IdentifierNameSyntax identifier => identifier.Identifier.Text,
        _ => string.Empty
    };

    /// <summary>Resolves <c>x =&gt; x.A.B</c> to <c>A.B</c>; null for any other lambda body.</summary>
    private static string? PropertyPath(InvocationExpressionSyntax root)
    {
        if (root.ArgumentList.Arguments.Count == 0)
        {
            return null;
        }

        string parameter;
        CSharpSyntaxNode body;
        switch (root.ArgumentList.Arguments[0].Expression)
        {
            case SimpleLambdaExpressionSyntax simple:
                parameter = simple.Parameter.Identifier.Text;
                body = simple.Body;
                break;
            case ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters.Count: 1 } parenthesized:
                parameter = parenthesized.ParameterList.Parameters[0].Identifier.Text;
                body = parenthesized.Body;
                break;
            default:
                return null;
        }

        var parts = new List<string>();
        var current = body as ExpressionSyntax;
        while (current is not null)
        {
            if (current is PostfixUnaryExpressionSyntax postfix && postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression))
            {
                current = postfix.Operand;
            }
            else if (current is MemberAccessExpressionSyntax member)
            {
                parts.Add(member.Name.Identifier.Text);
                current = member.Expression;
            }
            else
            {
                break;
            }
        }

        if (current is IdentifierNameSyntax identifier && identifier.Identifier.Text == parameter && parts.Count > 0)
        {
            parts.Reverse();
            return string.Join('.', parts);
        }

        return null;
    }
}
