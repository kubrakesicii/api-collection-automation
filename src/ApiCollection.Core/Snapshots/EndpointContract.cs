using System.Text.Json;
using System.Text.Json.Nodes;
using ApiCollection.Core.Models;

namespace ApiCollection.Core.Snapshots;

/// <summary>
/// A sorted set of plain "key = value" facts about an endpoint's contract, e.g. <c>parameter[path:id].type = int</c>.
/// Two sets are equal when they hold the same facts, so snapshots and results built from them compare by value.
/// A fact is omitted when its value is unknown (null); that is how "default was added/removed" shows up in a diff.
/// </summary>
public sealed class ContractFacts : Dictionary<string, string>, IEquatable<ContractFacts>
{
    public ContractFacts()
        : base(StringComparer.Ordinal)
    {
    }

    public bool Equals(ContractFacts? other) =>
        other is not null && Count == other.Count && this.All(fact => other.TryGetValue(fact.Key, out var value) && value == fact.Value);

    public override bool Equals(object? obj) => Equals(obj as ContractFacts);

    public override int GetHashCode() => Count;
}

/// <summary>
/// Turns an <see cref="EndpointDefinition"/> into <see cref="ContractFacts"/>: everything about the endpoint that reaches the
/// collection (names, route, parameters, body and its properties, responses, auth, validation, example values).
/// Where an example came from (file, line) is deliberately left out, so moving code never changes the contract.
///
/// Keys look like <c>entity.attribute</c>. The attribute is the text after the first '.' that follows the last ']'
/// (or after the first '.' when there is no ']'); entities are:
/// endpoint, parameters, parameter[loc:name], body, body.property[path], body.propertyOrder[parent],
/// response[status], auth, validation[property:rule].
/// </summary>
public static class EndpointContract
{
    public static ContractFacts From(EndpointDefinition endpoint)
    {
        var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var entities = new HashSet<string>(StringComparer.Ordinal);

        void Set(string key, string? value)
        {
            if (value is not null)
            {
                sorted[key] = value;
            }
        }

        void Flag(string key, bool value) => sorted[key] = value ? "true" : "false";

        // Entities that would collide (same parameter twice, same rule twice) get "#2", "#3" inside the brackets.
        string Entity(string prefix, string inside)
        {
            var candidate = $"{prefix}[{inside}]";
            for (var n = 2; !entities.Add(candidate); n++)
            {
                candidate = $"{prefix}[{inside}#{n}]";
            }

            return candidate;
        }

        void Example(string entity, ExampleDefinition? example)
        {
            if (example is null)
            {
                return;
            }

            Set(entity + ".example", Compact(example.Json));
            Flag(entity + ".exampleComplete", example.IsComplete);
            if (example.MissingFields.Count > 0)
            {
                Set(entity + ".exampleMissing", string.Join(',', example.MissingFields));
            }
        }

        void Properties(IReadOnlyList<PropertyDefinition> properties, string parent)
        {
            if (properties.Count > 1)
            {
                Set($"body.propertyOrder[{parent}].names", string.Join(',', properties.Select(p => p.Name)));
            }

            foreach (var property in properties)
            {
                var path = parent.Length == 0 ? property.Name : $"{parent}.{property.Name}";
                var entity = Entity("body.property", path);
                Set(entity + ".type", property.TypeName);
                Flag(entity + ".required", property.IsRequired);
                Flag(entity + ".nullable", property.IsNullable);
                Set(entity + ".jsonName", property.JsonName);
                Set(entity + ".example", property.Example is null ? null : Compact(property.Example.Json));
                Set(entity + ".default", property.DefaultValue is null ? null : Compact(property.DefaultValue.Json));
                Properties(property.Properties, path);
            }
        }

        Set("endpoint.controller", endpoint.ControllerName);
        Set("endpoint.action", endpoint.ActionName);
        Set("endpoint.route", endpoint.RouteTemplate);

        if (endpoint.Parameters.Count > 1)
        {
            Set("parameters.order", string.Join(',', endpoint.Parameters.Select(p => $"{p.Location.ToString().ToLowerInvariant()}:{p.Name}")));
        }

        foreach (var parameter in endpoint.Parameters)
        {
            var entity = Entity("parameter", $"{parameter.Location.ToString().ToLowerInvariant()}:{parameter.Name}");
            Set(entity + ".type", parameter.TypeName);
            Flag(entity + ".required", parameter.IsRequired);
            Set(entity + ".default", parameter.DefaultValue);
        }

        if (endpoint.RequestBody is { } body)
        {
            Set("body.type", body.TypeName);
            Set("body.contentType", body.ContentType);
            Flag("body.required", body.IsRequired);
            Properties(body.Properties, string.Empty);
            Example("body", body.Example);
        }

        foreach (var response in endpoint.Responses)
        {
            var entity = Entity("response", response.StatusCode.ToString());
            Set(entity + ".type", response.TypeName);
            Set(entity + ".contentType", response.ContentType);
            Example(entity, response.Example);
        }

        var auth = endpoint.Authentication;
        Flag("auth.required", auth.RequiresAuthentication);
        Set("auth.scheme", auth.Scheme);
        Set("auth.roles", auth.Roles.Count == 0 ? null : string.Join(',', auth.Roles));
        Set("auth.policies", auth.Policies.Count == 0 ? null : string.Join(',', auth.Policies));

        foreach (var rule in endpoint.Validation)
        {
            Set(Entity("validation", $"{rule.PropertyName}:{rule.RuleName}") + ".arguments", string.Join(',', rule.Arguments));
        }

        var facts = new ContractFacts();
        foreach (var (key, value) in sorted)
        {
            facts[key] = value;
        }

        return facts;
    }

    /// <summary>Splits a fact key into its entity and attribute (see the class remarks).</summary>
    public static (string Entity, string Attribute) Split(string key)
    {
        var closing = key.LastIndexOf(']');
        var dot = key.IndexOf('.', Math.Max(closing, 0));
        return dot < 0 ? (key, string.Empty) : (key[..dot], key[(dot + 1)..]);
    }

    private static string Compact(string json)
    {
        try
        {
            return JsonNode.Parse(json)?.ToJsonString() ?? "null";
        }
        catch (JsonException)
        {
            return json.Replace("\r\n", "\n", StringComparison.Ordinal);
        }
    }
}
