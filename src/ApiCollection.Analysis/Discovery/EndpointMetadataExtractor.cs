using System.Text.RegularExpressions;
using ApiCollection.Core.Models;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static ApiCollection.Analysis.Discovery.SyntaxHelpers;

namespace ApiCollection.Analysis.Discovery;

internal sealed record EndpointMetadata(
    IReadOnlyList<ParameterDefinition> Parameters,
    RequestBodyDefinition? RequestBody,
    AuthenticationDefinition Authentication,
    IReadOnlyList<ValidationRuleDefinition> Validation);

/// <summary>Extracts parameters, request body, authentication and validation metadata for one action.</summary>
internal sealed partial class EndpointMetadataExtractor
{
    private static readonly HashSet<string> IgnoredParameterTypes =
        ["CancellationToken", "HttpContext", "HttpRequest", "HttpResponse", "ClaimsPrincipal"];

    private readonly SourceCatalog _catalog;
    private readonly DtoModelBuilder _dto;
    private readonly ValidationExtractor _validation;

    public EndpointMetadataExtractor(SourceCatalog catalog, DtoModelBuilder dto)
    {
        _catalog = catalog;
        _dto = dto;
        _validation = new ValidationExtractor(catalog);
    }

    public EndpointMetadata Extract(
        MethodDeclarationSyntax method, IReadOnlyList<AttributeSyntax> classAttributes,
        string route, string owner, List<string> warnings)
    {
        var apiController = classAttributes.Any(a => AttributeName(a) == "ApiController");
        var (parameters, body, bodyType) = ExtractParameters(method, apiController, route, owner, warnings);
        var authentication = ExtractAuthentication(classAttributes, Attributes(method).ToList(), owner, warnings);
        var validation = bodyType is null ? [] : _validation.RulesFor(bodyType, warnings);
        return new EndpointMetadata(parameters, body, authentication, validation);
    }

    // ---- parameters and body ----

    private (List<ParameterDefinition> Parameters, RequestBodyDefinition? Body, string? BodyType) ExtractParameters(
        MethodDeclarationSyntax method, bool apiController, string route, string owner, List<string> warnings)
    {
        var pathNames = PathPlaceholder().Matches(route).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var parameters = new List<ParameterDefinition>();
        RequestBodyDefinition? body = null;
        string? bodyType = null;

        foreach (var parameter in method.ParameterList.Parameters)
        {
            if (parameter.Type is null)
            {
                continue;
            }

            var element = ElementType(parameter.Type);
            var typeName = LastIdentifier(element);
            var name = parameter.Identifier.Text;
            var skip = false;
            ParameterLocation? location = null;

            foreach (var attribute in parameter.AttributeLists.SelectMany(l => l.Attributes))
            {
                switch (AttributeName(attribute))
                {
                    case "FromRoute":
                        location = ParameterLocation.Path;
                        name = BindingName(attribute) ?? name;
                        break;
                    case "FromQuery":
                        location = ParameterLocation.Query;
                        name = BindingName(attribute) ?? name;
                        break;
                    case "FromHeader":
                        location = ParameterLocation.Header;
                        name = BindingName(attribute) ?? name;
                        break;
                    case "FromBody":
                        location = ParameterLocation.Body;
                        break;
                    case "FromServices":
                        skip = true;
                        break;
                    case "FromForm":
                        warnings.Add($"{owner}: parameter '{name}' uses [FromForm], which is not supported; it was skipped.");
                        skip = true;
                        break;
                }
            }

            if (skip)
            {
                continue;
            }

            if (location is null)
            {
                if (pathNames.Contains(name))
                {
                    location = ParameterLocation.Path;
                }
                else if (IgnoredParameterTypes.Contains(typeName))
                {
                    continue;
                }
                else if (_catalog.IsSimpleType(element))
                {
                    location = ParameterLocation.Query;
                }
                else if (_catalog.IsComplexType(element) && apiController)
                {
                    location = ParameterLocation.Body;
                }
                else
                {
                    warnings.Add(_catalog.IsComplexType(element)
                        ? $"{owner}: binding source of parameter '{name}' ({typeName}) cannot be inferred without [ApiController]; it was skipped."
                        : $"{owner}: binding source of parameter '{name}' ({typeName}) could not be determined; it was skipped.");
                    continue;
                }
            }

            var isNullable = IsNullableType(parameter.Type);
            var defaultValue = parameter.Default?.Value.ToString();

            if (location == ParameterLocation.Body)
            {
                if (body is not null)
                {
                    warnings.Add($"{owner}: multiple body parameters found; '{name}' was skipped.");
                    continue;
                }

                var required = !isNullable && parameter.Default is null;
                body = new RequestBodyDefinition
                {
                    TypeName = StripNullable(parameter.Type).ToString(),
                    IsRequired = required,
                    Properties = _dto.BuildForType(parameter.Type, owner, warnings)
                };
                bodyType = LastIdentifier(StripNullable(parameter.Type));
                parameters.Add(new ParameterDefinition
                {
                    Name = name,
                    Location = ParameterLocation.Body,
                    TypeName = parameter.Type.ToString(),
                    IsRequired = required,
                    DefaultValue = defaultValue
                });
            }
            else if (location == ParameterLocation.Query && _catalog.IsComplexType(element))
            {
                // A complex [FromQuery] model binds each of its properties as a separate query value.
                foreach (var property in _dto.BuildForType(parameter.Type, owner, warnings))
                {
                    if (property.Properties.Count > 0)
                    {
                        warnings.Add($"{owner}: nested property '{property.Name}' of query model '{typeName}' is not supported; it was skipped.");
                        continue;
                    }

                    parameters.Add(new ParameterDefinition
                    {
                        Name = property.Name,
                        Location = ParameterLocation.Query,
                        TypeName = property.TypeName,
                        IsRequired = property.IsRequired
                    });
                }
            }
            else
            {
                parameters.Add(new ParameterDefinition
                {
                    Name = name,
                    Location = location.Value,
                    TypeName = parameter.Type.ToString(),
                    IsRequired = location == ParameterLocation.Path || (!isNullable && parameter.Default is null),
                    DefaultValue = defaultValue
                });
            }
        }

        return (parameters, body, bodyType);
    }

    private static string? BindingName(AttributeSyntax attribute)
    {
        var argument = attribute.ArgumentList?.Arguments.FirstOrDefault(a => a.NameEquals?.Name.Identifier.Text == "Name");
        return argument is not null && TryEvaluate(argument.Expression, out var name) ? name : null;
    }

    [GeneratedRegex(@"\{\*{0,2}([A-Za-z_]\w*)")]
    private static partial Regex PathPlaceholder();

    // ---- authentication ----

    private static AuthenticationDefinition ExtractAuthentication(
        IReadOnlyList<AttributeSyntax> classAttributes, List<AttributeSyntax> methodAttributes,
        string owner, List<string> warnings)
    {
        var all = classAttributes.Concat(methodAttributes).ToList();
        if (all.Any(a => AttributeName(a) == "AllowAnonymous"))
        {
            return new AuthenticationDefinition { RequiresAuthentication = false };
        }

        var authorize = all.Where(a => AttributeName(a) == "Authorize").ToList();
        if (authorize.Count == 0)
        {
            return new AuthenticationDefinition();
        }

        List<string> roles = [];
        List<string> policies = [];
        List<string> schemes = [];
        foreach (var attribute in authorize)
        {
            foreach (var argument in attribute.ArgumentList?.Arguments ?? [])
            {
                var target = argument.NameEquals?.Name.Identifier.Text ?? argument.NameColon?.Name.Identifier.Text ?? "Policy";
                if (!TryEvaluate(argument.Expression, out var value))
                {
                    warnings.Add($"{owner}: [Authorize] argument '{target}' is not a string literal and was skipped.");
                    continue;
                }

                if (target.Equals("Roles", StringComparison.OrdinalIgnoreCase))
                {
                    roles.AddRange(Split(value));
                }
                else if (target.Equals("Policy", StringComparison.OrdinalIgnoreCase))
                {
                    policies.Add(value);
                }
                else if (target.Equals("AuthenticationSchemes", StringComparison.OrdinalIgnoreCase))
                {
                    schemes.AddRange(Split(value));
                }
            }
        }

        return new AuthenticationDefinition
        {
            RequiresAuthentication = true,
            Scheme = schemes.Count == 0 ? null : string.Join(',', schemes.Distinct()),
            Roles = roles.Distinct().ToList(),
            Policies = policies.Distinct().ToList()
        };
    }

    private static IEnumerable<string> Split(string value) =>
        value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
