using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ApiCollection.Core.Models;
using ApiCollection.Postman.Models;

namespace ApiCollection.Postman.Generation;

public sealed record PostmanCollectionOptions
{
    public string Name { get; init; } = "API Collection";

    /// <summary>Default value of the <c>baseUrl</c> collection variable (not a secret).</summary>
    public string BaseUrl { get; init; } = "http://localhost:5000";
}

/// <summary>
/// Builds a Postman Collection v2.1 model from endpoint definitions: one folder per controller, one request per endpoint.
/// Pure and offline; it never talks to Postman and never writes a token (only the <c>{{token}}</c> placeholder).
/// </summary>
public sealed partial class PostmanCollectionGenerator
{
    public const string BaseUrlVariable = "baseUrl";
    public const string TokenVariable = "token";

    public PostmanCollection Generate(IEnumerable<EndpointDefinition> endpoints, PostmanCollectionOptions? options = null)
    {
        options ??= new PostmanCollectionOptions();

        var folders = endpoints
            .GroupBy(e => e.ControllerName)
            .Select(group => new PostmanFolder { Name = group.Key, Items = BuildItems(group.ToList()) })
            .ToList();

        return new PostmanCollection
        {
            Info = new PostmanInfo
            {
                Name = options.Name,
                Description = $"Generated from endpoint metadata. Set the `{BaseUrlVariable}` variable and, for protected endpoints, the `{TokenVariable}` variable."
            },
            Items = folders,
            Variables =
            [
                new PostmanVariable { Key = BaseUrlVariable, Value = options.BaseUrl, Description = "Base address of the API." },
                new PostmanVariable { Key = TokenVariable, Value = string.Empty, Description = "Bearer token for protected endpoints. Never commit a real token." }
            ]
        };
    }

    private static List<PostmanRequestItem> BuildItems(List<EndpointDefinition> endpoints)
    {
        var duplicateNames = endpoints.GroupBy(e => e.ActionName).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        return endpoints.Select(e => new PostmanRequestItem
        {
            Name = duplicateNames.Contains(e.ActionName) ? $"{e.ActionName} [{Method(e)} /{e.RouteTemplate}]" : e.ActionName,
            Request = BuildRequest(e)
        }).ToList();
    }

    private static string Method(EndpointDefinition endpoint) => endpoint.HttpMethod.ToString().ToUpperInvariant();

    // ---- request ----

    private static PostmanRequest BuildRequest(EndpointDefinition endpoint)
    {
        var headers = endpoint.Parameters
            .Where(p => p.Location == ParameterLocation.Header)
            .Select(p => new PostmanHeader
            {
                Key = p.Name,
                Description = Describe(p),
                Disabled = p.IsRequired ? null : true
            })
            .ToList();

        var body = BuildBody(endpoint, out var bodyNotes);
        if (body is not null)
        {
            headers.Insert(0, new PostmanHeader { Key = "Content-Type", Value = endpoint.RequestBody!.ContentType });
        }

        return new PostmanRequest
        {
            Method = Method(endpoint),
            Headers = headers,
            Url = BuildUrl(endpoint),
            Body = body,
            Auth = BuildAuth(endpoint.Authentication),
            Description = BuildDescription(endpoint, bodyNotes)
        };
    }

    private static PostmanUrl BuildUrl(EndpointDefinition endpoint)
    {
        var segments = endpoint.RouteTemplate
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => RoutePlaceholder().Replace(segment, m => ":" + m.Groups[1].Value))
            .ToList();

        var pathVariables = RoutePlaceholder().Matches(endpoint.RouteTemplate)
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .Select(name => new PostmanVariable
            {
                Key = name,
                Description = endpoint.Parameters
                    .FirstOrDefault(p => p.Location == ParameterLocation.Path && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) is { } p
                    ? Describe(p)
                    : null
            })
            .ToList();

        var query = endpoint.Parameters
            .Where(p => p.Location == ParameterLocation.Query)
            .Select(p => new PostmanQueryParameter
            {
                Key = p.Name,
                Value = string.Empty,
                Description = Describe(p),
                Disabled = p.IsRequired ? null : true
            })
            .ToList();

        var raw = "{{" + BaseUrlVariable + "}}" + (segments.Count == 0 ? string.Empty : "/" + string.Join('/', segments));
        var enabled = query.Where(q => q.Disabled is null).ToList();
        if (enabled.Count > 0)
        {
            raw += "?" + string.Join('&', enabled.Select(q => q.Key + "="));
        }

        return new PostmanUrl
        {
            Raw = raw,
            Host = ["{{" + BaseUrlVariable + "}}"],
            Path = segments,
            Query = query.Count == 0 ? null : query,
            Variable = pathVariables.Count == 0 ? null : pathVariables
        };
    }

    private static PostmanAuth? BuildAuth(AuthenticationDefinition authentication)
    {
        if (!authentication.RequiresAuthentication || !IsBearerCompatible(authentication.Scheme))
        {
            return null;
        }

        return new PostmanAuth
        {
            Bearer = [new PostmanAuthParameter { Key = "token", Value = "{{" + TokenVariable + "}}" }]
        };
    }

    private static bool IsBearerCompatible(string? scheme) =>
        scheme is null || scheme.Contains("Bearer", StringComparison.OrdinalIgnoreCase);

    // ---- body ----

    private static PostmanBody? BuildBody(EndpointDefinition endpoint, out List<string> notes)
    {
        notes = [];
        var body = endpoint.RequestBody;
        if (body is null)
        {
            return null;
        }

        if (body.Example is { } example)
        {
            notes.Add($"Body example ({(example.IsComplete ? "complete" : "incomplete")}); sources:");
            notes.AddRange(example.Fields.Select(f => $"- `{f.Path}`: {f.Source} ({f.SourceDetail})"));
            if (example.MissingFields.Count > 0)
            {
                notes.Add($"Fields without an example (omitted): {string.Join(", ", example.MissingFields.Select(f => $"`{f}`"))}.");
            }

            return new PostmanBody { Raw = Lf(example.Json) };
        }

        notes.Add("No example was found for this body. The JSON is a skeleton generated from the DTO contract; values such as `<string>` are placeholders, not real examples.");
        return new PostmanBody { Raw = Lf(SkeletonValue(body.TypeName, body.Properties).ToJsonString(SkeletonOptions)) };
    }

    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static readonly JsonSerializerOptions SkeletonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static JsonNode SkeletonValue(string typeName, IReadOnlyList<PropertyDefinition> properties)
    {
        var element = ElementType(typeName, out var isCollection);
        JsonNode value = properties.Count > 0
            ? SkeletonObject(properties)
            : JsonValue.Create("<" + element + ">");
        return isCollection ? new JsonArray(value) : value;
    }

    private static JsonObject SkeletonObject(IReadOnlyList<PropertyDefinition> properties)
    {
        var result = new JsonObject();
        foreach (var property in properties)
        {
            result[property.JsonName ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name)] =
                SkeletonValue(property.TypeName, property.Properties);
        }

        return result;
    }

    /// <summary>Strips nullable and collection wrappers from a type name (e.g. <c>List&lt;Tag&gt;?</c> gives <c>Tag</c>).</summary>
    private static string ElementType(string typeName, out bool isCollection)
    {
        isCollection = false;
        var name = typeName.Trim().TrimEnd('?');
        if (name.EndsWith("[]", StringComparison.Ordinal))
        {
            isCollection = true;
            return name[..^2].TrimEnd('?');
        }

        var match = CollectionType().Match(name);
        if (match.Success)
        {
            isCollection = true;
            return match.Groups[1].Value.Trim().TrimEnd('?');
        }

        return name;
    }

    // ---- description ----

    private static string BuildDescription(EndpointDefinition endpoint, List<string> bodyNotes)
    {
        var text = new StringBuilder();
        text.AppendLine($"**{Method(endpoint)}** `/{endpoint.RouteTemplate}`");
        text.AppendLine();
        text.AppendLine($"Controller: `{endpoint.ControllerName}` · Action: `{endpoint.ActionName}`");

        var auth = endpoint.Authentication;
        if (auth.RequiresAuthentication)
        {
            var details = new List<string>();
            if (auth.Roles.Count > 0)
            {
                details.Add($"roles: {string.Join(", ", auth.Roles)}");
            }

            if (auth.Policies.Count > 0)
            {
                details.Add($"policies: {string.Join(", ", auth.Policies)}");
            }

            text.AppendLine();
            text.AppendLine($"Authentication: **required**{(details.Count == 0 ? "" : " (" + string.Join("; ", details) + ")")}.");
            text.AppendLine(IsBearerCompatible(auth.Scheme)
                ? $"Uses the Bearer token from the `{{{{{TokenVariable}}}}}` variable."
                : $"Scheme `{auth.Scheme}` is not supported by this generator; configure authorization manually.");
        }

        var parameters = endpoint.Parameters.Where(p => p.Location != ParameterLocation.Body).ToList();
        if (parameters.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Parameters:");
            foreach (var parameter in parameters)
            {
                text.AppendLine($"- `{parameter.Name}` ({parameter.Location.ToString().ToLowerInvariant()}): {Describe(parameter)}");
            }
        }

        if (bodyNotes.Count > 0)
        {
            text.AppendLine();
            foreach (var note in bodyNotes)
            {
                text.AppendLine(note);
            }
        }

        if (endpoint.Validation.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Validation rules:");
            foreach (var rule in endpoint.Validation)
            {
                text.AppendLine($"- `{rule.PropertyName}`: {rule.RuleName}({string.Join(", ", rule.Arguments)})");
            }
        }

        foreach (var response in endpoint.Responses.Where(r => r.Example is not null))
        {
            text.AppendLine();
            text.AppendLine($"Example response {response.StatusCode} (from {response.Example!.Fields.FirstOrDefault()?.Source}; not a recorded response):");
            text.AppendLine("```json");
            text.AppendLine(Lf(response.Example.Json));
            text.AppendLine("```");
        }

        return Lf(text.ToString().TrimEnd());
    }

    private static string Describe(ParameterDefinition parameter)
    {
        var parts = new List<string> { parameter.TypeName, parameter.IsRequired ? "required" : "optional" };
        if (parameter.DefaultValue is not null)
        {
            parts.Add($"default {parameter.DefaultValue}");
        }

        return string.Join(", ", parts);
    }

    [GeneratedRegex(@"\{\*{0,2}([A-Za-z_]\w*)[^}]*\}")]
    private static partial Regex RoutePlaceholder();

    [GeneratedRegex(@"^(?:[\w.]+\.)?(?:List|IList|IEnumerable|IReadOnlyList|ICollection|IReadOnlyCollection|HashSet|ISet)<(.+)>$")]
    private static partial Regex CollectionType();
}
