using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApiCollection.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static ApiCollection.Analysis.Discovery.SyntaxHelpers;

namespace ApiCollection.Analysis.Discovery;

/// <summary>
/// Builds request and response examples. Every value must come from a traceable source (static code or curated metadata)
/// and must satisfy the DTO contract; anything else is reported as a diagnostic, never invented.
/// </summary>
internal sealed class ExampleBuilder(
    SourceCatalog catalog, DtoModelBuilder dto, ExampleMetadata? metadata,
    List<ExampleDiagnostic> diagnostics, List<string> warnings)
{
    private static readonly Dictionary<string, int> ResponseHelpers = new()
    {
        ["Ok"] = 200,
        ["Created"] = 201,
        ["CreatedAtAction"] = 201,
        ["CreatedAtRoute"] = 201
    };

    // ---- request examples ----

    public ExampleDefinition? BuildRequestExample(string endpointId, RequestBodyDefinition body)
    {
        var bodyType = SyntaxFactory.ParseTypeName(body.TypeName);
        var typeName = LastIdentifier(ElementType(bodyType));

        if (metadata is not null && metadata.RequestExamples.TryGetValue(endpointId, out var raw))
        {
            var detail = $"{metadata.SourceName}: requests[{endpointId}]";
            var errors = new List<string>();
            if (!ExampleJson.TryParse(raw, out var node))
            {
                errors.Add("not valid JSON");
            }
            else
            {
                Validate(node, bodyType, body.Properties, "$", errors);
            }

            if (errors.Count == 0)
            {
                return new ExampleDefinition
                {
                    Json = ExampleJson.Pretty(ExampleJson.TryParse(raw, out var parsed) ? parsed : null),
                    IsComplete = true,
                    Fields = [new ExampleFieldSource { Path = "$", Source = ExampleSource.ExternalMetadata, SourceDetail = detail }]
                };
            }

            foreach (var error in errors)
            {
                diagnostics.Add(new ExampleDiagnostic("EX003", endpointId, "$", $"{detail} was rejected: {error}"));
            }
        }

        if (body.Properties.Count == 0)
        {
            diagnostics.Add(new ExampleDiagnostic(
                "EX001", endpointId, typeName,
                $"request body type '{typeName}' has no extractable properties; provide requests['{endpointId}'] in the example metadata."));
            return null;
        }

        var fields = new List<ExampleFieldSource>();
        var missing = new List<string>();
        var composed = Compose(body.Properties, typeName, string.Empty, endpointId, fields, missing);
        if (composed is null)
        {
            diagnostics.Add(new ExampleDiagnostic(
                "EX001", endpointId, typeName,
                $"no example values were found for request body '{typeName}'; add them to the source or to the example metadata."));
            return null;
        }

        JsonNode root = IsCollection(bodyType) ? new JsonArray(composed) : composed;
        return new ExampleDefinition
        {
            Json = ExampleJson.Pretty(root),
            IsComplete = missing.Count == 0,
            Fields = fields,
            MissingFields = missing
        };
    }

    private JsonObject? Compose(
        IReadOnlyList<PropertyDefinition> properties, string typeName, string prefix, string endpointId,
        List<ExampleFieldSource> fields, List<string> missing)
    {
        var result = new JsonObject();
        foreach (var property in properties)
        {
            var path = prefix + property.Name;
            var propertyType = SyntaxFactory.ParseTypeName(property.TypeName);
            JsonNode? value = null;
            var found = false;

            foreach (var candidate in Candidates(property, typeName))
            {
                var errors = new List<string>();
                if (!ExampleJson.TryParse(candidate.Json, out var node))
                {
                    errors.Add("not valid JSON");
                }
                else
                {
                    Validate(node, propertyType, property.Properties, path, errors);
                }

                if (errors.Count > 0)
                {
                    foreach (var error in errors)
                    {
                        diagnostics.Add(new ExampleDiagnostic(
                            "EX003", endpointId, $"{typeName}.{property.Name}", $"{candidate.SourceDetail} was rejected: {error}"));
                    }

                    continue;
                }

                value = node;
                found = true;
                fields.Add(new ExampleFieldSource { Path = path, Source = candidate.Source, SourceDetail = candidate.SourceDetail });
                break;
            }

            if (!found && property.Properties.Count > 0)
            {
                var missingBefore = missing.Count;
                var nestedType = LastIdentifier(ElementType(propertyType));
                var nested = Compose(property.Properties, nestedType, path + ".", endpointId, fields, missing);
                if (nested is not null)
                {
                    value = IsCollection(propertyType) ? new JsonArray(nested) : nested;
                    found = true;
                }
                else if (missing.Count > missingBefore)
                {
                    continue;
                }
            }

            if (!found)
            {
                missing.Add(path);
                diagnostics.Add(new ExampleDiagnostic(
                    "EX002", endpointId, path,
                    $"no example for '{typeName}.{property.Name}': nothing in source (XML <example>, [DefaultValue], initializer) or example metadata."));
                continue;
            }

            result[JsonNameOf(property)] = value;
        }

        return result.Count == 0 ? null : result;
    }

    /// <summary>Candidate values in priority order: curated metadata, documented example, declared default.</summary>
    private IEnumerable<ExampleValue> Candidates(PropertyDefinition property, string typeName)
    {
        var key = $"{typeName}.{property.Name}";
        if (metadata is not null && metadata.PropertyExamples.TryGetValue(key, out var raw))
        {
            yield return new ExampleValue
            {
                Json = raw,
                Source = ExampleSource.ExternalMetadata,
                SourceDetail = $"{metadata.SourceName}: properties[{key}]"
            };
        }

        if (property.Example is not null)
        {
            yield return property.Example;
        }

        if (property.DefaultValue is not null)
        {
            yield return property.DefaultValue;
        }
    }

    private static string JsonNameOf(PropertyDefinition property) =>
        property.JsonName ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name);

    // ---- response examples ----

    private readonly record struct ResponseCandidate(int Status, ExpressionSyntax Value, SyntaxNode Site);

    public List<ResponseDefinition> BuildResponses(string endpointId, MethodDeclarationSyntax method, string owner)
    {
        var responses = new List<ResponseDefinition>();

        if (metadata is not null && metadata.ResponseExamples.TryGetValue(endpointId, out var byStatus))
        {
            foreach (var (status, raw) in byStatus.OrderBy(e => e.Key))
            {
                var detail = $"{metadata.SourceName}: responses[{endpointId}][{status}]";
                if (!ExampleJson.TryParse(raw, out var node))
                {
                    diagnostics.Add(new ExampleDiagnostic("EX003", endpointId, status.ToString(), $"{detail} was rejected: not valid JSON"));
                    continue;
                }

                responses.Add(new ResponseDefinition
                {
                    StatusCode = status,
                    ContentType = "application/json",
                    Example = new ExampleDefinition
                    {
                        Json = ExampleJson.Pretty(node),
                        IsComplete = true,
                        Fields = [new ExampleFieldSource { Path = "$", Source = ExampleSource.ExternalMetadata, SourceDetail = detail }]
                    }
                });
            }
        }

        var candidates = ResponseCandidates(method).ToList();
        foreach (var candidate in candidates)
        {
            if (responses.Any(r => r.StatusCode == candidate.Status))
            {
                continue;
            }

            var unresolved = new List<string>();
            if (!TryEvaluate(candidate.Value, string.Empty, owner, unresolved, out var node) || unresolved.Count > 0
                || node is not (JsonObject or JsonArray))
            {
                if (unresolved.Count == 0)
                {
                    unresolved.Add("only object and array response bodies are supported");
                }

                diagnostics.Add(new ExampleDiagnostic(
                    "EX004", endpointId, $"{candidate.Status}",
                    $"{owner}: no response example for status {candidate.Status} ({Describe(candidate.Site)}): {string.Join("; ", unresolved)}."));
                continue;
            }

            responses.Add(new ResponseDefinition
            {
                StatusCode = candidate.Status,
                TypeName = TypeNameOf(candidate.Value),
                ContentType = "application/json",
                Example = new ExampleDefinition
                {
                    Json = ExampleJson.Pretty(node),
                    IsComplete = true,
                    Fields = [new ExampleFieldSource
                    {
                        Path = "$",
                        Source = ExampleSource.ResponseLiteral,
                        SourceDetail = $"{Describe(candidate.Site)}: literal response returned by {owner}"
                    }]
                }
            });
        }

        if (responses.Count == 0 && candidates.Count == 0 && ExpectsBody(method))
        {
            diagnostics.Add(new ExampleDiagnostic(
                "EX004", endpointId, "response",
                $"{owner}: the response body is not a static literal (no Ok/Created with a literal object); add responses['{endpointId}'] to the example metadata."));
        }

        return responses.OrderBy(r => r.StatusCode).ToList();
    }

    private static IEnumerable<ResponseCandidate> ResponseCandidates(MethodDeclarationSyntax method)
    {
        var returned = new List<ExpressionSyntax>();
        if (method.ExpressionBody is { } expressionBody)
        {
            returned.Add(expressionBody.Expression);
        }
        else if (method.Body is { } block)
        {
            returned.AddRange(block
                .DescendantNodes(n => n is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax)
                .OfType<ReturnStatementSyntax>()
                .Select(r => r.Expression)
                .OfType<ExpressionSyntax>());
        }

        foreach (var expression in returned)
        {
            var current = expression;
            while (current is ParenthesizedExpressionSyntax parenthesized)
            {
                current = parenthesized.Expression;
            }

            if (current is InvocationExpressionSyntax invocation
                && HelperName(invocation) is { } name && ResponseHelpers.TryGetValue(name, out var status))
            {
                var arguments = invocation.ArgumentList.Arguments;
                var hasBody = name == "Ok" ? arguments.Count == 1 : arguments.Count >= 2;
                if (hasBody)
                {
                    yield return new ResponseCandidate(status, arguments.Last().Expression, invocation);
                }
            }
            else if (current is ObjectCreationExpressionSyntax or ArrayCreationExpressionSyntax or ImplicitArrayCreationExpressionSyntax)
            {
                yield return new ResponseCandidate(200, current, current);
            }
        }
    }

    private static string? HelperName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.Text,
        MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } member => member.Name.Identifier.Text,
        _ => null
    };

    /// <summary>False for IActionResult/void actions, which may legitimately have no response body.</summary>
    private static bool ExpectsBody(MethodDeclarationSyntax method)
    {
        var type = method.ReturnType;
        if (type is GenericNameSyntax { Identifier.Text: "Task" or "ValueTask" } task && task.TypeArgumentList.Arguments.Count == 1)
        {
            type = task.TypeArgumentList.Arguments[0];
        }
        else if (type is IdentifierNameSyntax { Identifier.Text: "Task" or "ValueTask" })
        {
            return false;
        }

        // ActionResult<T> (generic) and plain DTO return types carry a body; IActionResult/ActionResult/void may not.
        return type is GenericNameSyntax || LastIdentifier(type) is not ("IActionResult" or "ActionResult" or "void");
    }

    private static string? TypeNameOf(ExpressionSyntax expression) => expression switch
    {
        ObjectCreationExpressionSyntax creation => creation.Type.ToString(),
        ArrayCreationExpressionSyntax array => array.Type.ToString(),
        ImplicitArrayCreationExpressionSyntax { Initializer.Expressions: [ObjectCreationExpressionSyntax first, ..] } => first.Type + "[]",
        InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name: GenericNameSyntax generic } }
            when generic.TypeArgumentList.Arguments.Count == 1 => generic.TypeArgumentList.Arguments[0] + "[]",
        _ => null
    };

    // ---- literal evaluation ----

    private static string Label(string path) => path.Length == 0 ? "$" : path;

    private bool TryEvaluate(ExpressionSyntax expression, string path, string owner, List<string> unresolved, out JsonNode? node)
    {
        node = null;
        switch (expression)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return TryEvaluate(parenthesized.Expression, path, owner, unresolved, out node);

            case ObjectCreationExpressionSyntax creation when IsCollection(creation.Type):
                return TryEvaluateElements(creation.Initializer?.Expressions, path, owner, unresolved, out node);

            case ObjectCreationExpressionSyntax creation:
                return TryEvaluateObject(creation, path, owner, unresolved, out node);

            case ArrayCreationExpressionSyntax { Initializer: not null } array:
                return TryEvaluateElements(array.Initializer.Expressions, path, owner, unresolved, out node);

            case ImplicitArrayCreationExpressionSyntax implicitArray:
                return TryEvaluateElements(implicitArray.Initializer.Expressions, path, owner, unresolved, out node);

            case InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "Array" }, Name: GenericNameSyntax { Identifier.Text: "Empty" } }
            }:
                node = new JsonArray();
                return true;

            default:
                if (ExampleJson.TryLiteralNode(expression, out node))
                {
                    return true;
                }

                unresolved.Add($"{Label(path)}: '{Shorten(expression)}' is not a static literal");
                return false;
        }
    }

    private bool TryEvaluateElements(
        SeparatedSyntaxList<ExpressionSyntax>? elements, string path, string owner, List<string> unresolved, out JsonNode? node)
    {
        var array = new JsonArray();
        var ok = true;
        var index = 0;
        foreach (var element in elements ?? default)
        {
            if (TryEvaluate(element, $"{path}[{index}]", owner, unresolved, out var value))
            {
                array.Add(value);
            }
            else
            {
                ok = false;
            }

            index++;
        }

        node = array;
        return ok;
    }

    private bool TryEvaluateObject(
        ObjectCreationExpressionSyntax creation, string path, string owner, List<string> unresolved, out JsonNode? node)
    {
        node = null;
        var typeName = LastIdentifier(creation.Type);
        var properties = dto.BuildForType(creation.Type, owner, warnings);
        if (properties.Count == 0)
        {
            unresolved.Add($"{Label(path)}: type '{typeName}' has no known properties");
            return false;
        }

        var parameterNames = catalog.FindTypes(typeName).OfType<RecordDeclarationSyntax>()
            .SelectMany(r => r.ParameterList?.Parameters ?? [])
            .Select(p => p.Identifier.Text)
            .ToList();

        var assigned = new Dictionary<string, ExpressionSyntax>();
        var arguments = creation.ArgumentList?.Arguments.ToList() ?? [];
        for (var i = 0; i < arguments.Count; i++)
        {
            var name = arguments[i].NameColon?.Name.Identifier.Text ?? (i < parameterNames.Count ? parameterNames[i] : null);
            if (name is null)
            {
                unresolved.Add($"{Label(path)}: constructor argument {i + 1} of '{typeName}' cannot be mapped to a property");
                return false;
            }

            assigned[name] = arguments[i].Expression;
        }

        foreach (var initializer in creation.Initializer?.Expressions.ToList() ?? [])
        {
            if (initializer is AssignmentExpressionSyntax { Left: IdentifierNameSyntax left } assignment)
            {
                assigned[left.Identifier.Text] = assignment.Right;
            }
            else
            {
                unresolved.Add($"{Label(path)}: initializer '{Shorten(initializer)}' of '{typeName}' is not a property assignment");
                return false;
            }
        }

        var result = new JsonObject();
        var ok = true;
        foreach (var property in properties)
        {
            var propertyPath = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
            JsonNode? value;
            if (assigned.TryGetValue(property.Name, out var expression))
            {
                if (!TryEvaluate(expression, propertyPath, owner, unresolved, out value))
                {
                    ok = false;
                    continue;
                }

                var errors = new List<string>();
                Validate(value, SyntaxFactory.ParseTypeName(property.TypeName), property.Properties, propertyPath, errors);
                if (errors.Count > 0)
                {
                    unresolved.AddRange(errors);
                    ok = false;
                    continue;
                }
            }
            else if (property.DefaultValue is not null && ExampleJson.TryParse(property.DefaultValue.Json, out value))
            {
                // A literal initializer / constructor default is the actual value the serializer will emit.
            }
            else
            {
                unresolved.Add($"{propertyPath}: not assigned and has no literal default");
                ok = false;
                continue;
            }

            result[JsonNameOf(property)] = value;
        }

        node = result;
        return ok;
    }

    // ---- contract validation ----

    private void Validate(JsonNode? node, TypeSyntax type, IReadOnlyList<PropertyDefinition> nested, string path, List<string> errors)
    {
        if (node is null)
        {
            if (!IsNullableType(type))
            {
                errors.Add($"{path}: null is not allowed for non-nullable '{type}'");
            }

            return;
        }

        var baseType = StripNullable(type);
        var element = CollectionElement(baseType);
        if (element is not null)
        {
            if (node is not JsonArray array)
            {
                errors.Add($"{path}: expected an array for '{type}'");
                return;
            }

            for (var i = 0; i < array.Count; i++)
            {
                Validate(array[i], element, nested, $"{path}[{i}]", errors);
            }

            return;
        }

        if (nested.Count > 0)
        {
            ValidateObject(node, nested, path, errors);
            return;
        }

        ValidateScalar(node, LastIdentifier(baseType), path, errors);
    }

    private void ValidateObject(JsonNode node, IReadOnlyList<PropertyDefinition> nested, string path, List<string> errors)
    {
        if (node is not JsonObject obj)
        {
            errors.Add($"{path}: expected an object");
            return;
        }

        static bool Matches(PropertyDefinition property, string key) =>
            string.Equals(JsonNameOf(property), key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase);

        foreach (var (key, value) in obj)
        {
            var property = nested.FirstOrDefault(p => Matches(p, key));
            if (property is null)
            {
                errors.Add($"{path}.{key}: unknown property");
                continue;
            }

            Validate(value, SyntaxFactory.ParseTypeName(property.TypeName), property.Properties, $"{path}.{key}", errors);
        }

        foreach (var required in nested.Where(p => p.IsRequired && !obj.Any(e => Matches(p, e.Key))))
        {
            errors.Add($"{path}.{JsonNameOf(required)}: required property is missing");
        }
    }

    private void ValidateScalar(JsonNode node, string typeName, string path, List<string> errors)
    {
        var kind = node.GetValueKind();

        if (ExampleJson.IntegerTypes.Contains(typeName))
        {
            if (kind != JsonValueKind.Number
                || !decimal.TryParse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                || number != decimal.Truncate(number) || !InRange(typeName, number))
            {
                errors.Add($"{path}: expected an integer within the range of '{typeName}'");
            }
        }
        else if (ExampleJson.FloatingTypes.Contains(typeName))
        {
            if (kind != JsonValueKind.Number)
            {
                errors.Add($"{path}: expected a number for '{typeName}'");
            }
        }
        else if (ExampleJson.BooleanTypes.Contains(typeName))
        {
            if (kind is not (JsonValueKind.True or JsonValueKind.False))
            {
                errors.Add($"{path}: expected a boolean");
            }
        }
        else if (ExampleJson.StringTypes.Contains(typeName))
        {
            if (kind != JsonValueKind.String)
            {
                errors.Add($"{path}: expected a string for '{typeName}'");
            }
            else if (!IsValidString(typeName, node.GetValue<string>()))
            {
                errors.Add($"{path}: '{node.GetValue<string>()}' is not a valid {typeName}");
            }
        }
        else if (catalog.IsEnum(typeName))
        {
            var members = catalog.EnumMembers(typeName);
            var valid = kind == JsonValueKind.Number
                || (kind == JsonValueKind.String && members.Contains(node.GetValue<string>(), StringComparer.OrdinalIgnoreCase));
            if (!valid)
            {
                errors.Add($"{path}: expected a member of enum '{typeName}' ({string.Join(", ", members)})");
            }
        }
    }

    private static bool IsValidString(string typeName, string value) => typeName switch
    {
        "Guid" => Guid.TryParse(value, out _),
        "DateTime" or "DateTimeOffset" => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        "DateOnly" => DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        "TimeOnly" => TimeOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        "TimeSpan" => TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out _),
        "Uri" => Uri.TryCreate(value, UriKind.RelativeOrAbsolute, out _),
        "char" or "Char" => value.Length == 1,
        _ => true
    };

    private static bool InRange(string typeName, decimal value) => typeName switch
    {
        "byte" or "Byte" => value is >= byte.MinValue and <= byte.MaxValue,
        "sbyte" or "SByte" => value is >= sbyte.MinValue and <= sbyte.MaxValue,
        "short" or "Int16" => value is >= short.MinValue and <= short.MaxValue,
        "ushort" or "UInt16" => value is >= ushort.MinValue and <= ushort.MaxValue,
        "int" or "Int32" => value is >= int.MinValue and <= int.MaxValue,
        "uint" or "UInt32" => value is >= uint.MinValue and <= uint.MaxValue,
        "long" or "Int64" => value is >= long.MinValue and <= long.MaxValue,
        "ulong" or "UInt64" => value is >= ulong.MinValue and <= ulong.MaxValue,
        _ => true
    };

    // ---- metadata reconciliation ----

    /// <summary>Reports metadata entries that match no discovered endpoint, type or property.</summary>
    public void ReportUnmatchedMetadata(IReadOnlyList<EndpointDefinition> endpoints)
    {
        if (metadata is null)
        {
            return;
        }

        var ids = endpoints.Select(e => e.Id).ToHashSet();
        foreach (var id in metadata.RequestExamples.Keys.Where(k => !ids.Contains(k)))
        {
            diagnostics.Add(new ExampleDiagnostic("EX005", id, "requests", $"{metadata.SourceName}: requests['{id}'] matches no discovered endpoint."));
        }

        foreach (var id in metadata.ResponseExamples.Keys.Where(k => !ids.Contains(k)))
        {
            diagnostics.Add(new ExampleDiagnostic("EX005", id, "responses", $"{metadata.SourceName}: responses['{id}'] matches no discovered endpoint."));
        }

        foreach (var key in metadata.PropertyExamples.Keys)
        {
            var dot = key.LastIndexOf('.');
            if (dot <= 0 || dot == key.Length - 1)
            {
                diagnostics.Add(new ExampleDiagnostic("EX005", null, key, $"{metadata.SourceName}: properties['{key}'] must be written as 'Type.Property'."));
                continue;
            }

            var typeName = key[..dot];
            var propertyName = key[(dot + 1)..];
            if (catalog.FindTypes(typeName).Count == 0)
            {
                diagnostics.Add(new ExampleDiagnostic("EX005", null, key, $"{metadata.SourceName}: properties['{key}']: type '{typeName}' was not found in the analyzed source."));
            }
            else if (!dto.BuildForType(SyntaxFactory.ParseTypeName(typeName), "example metadata", warnings).Any(p => p.Name == propertyName))
            {
                diagnostics.Add(new ExampleDiagnostic("EX005", null, key, $"{metadata.SourceName}: properties['{key}']: '{typeName}' has no property '{propertyName}'."));
            }
        }
    }
}
