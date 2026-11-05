using System.Security.Cryptography;
using System.Text;
using ApiCollection.Core.Models;

namespace ApiCollection.Core.Snapshots;

/// <summary>
/// A hash over everything about an endpoint that ends up in the generated collection, used to tell "Unchanged" from "Modified".
/// Where an example came from (file names, line numbers) is deliberately excluded: moving or reformatting code must not
/// make an endpoint look modified. The example values themselves are included.
/// </summary>
public static class EndpointFingerprint
{
    public static string Compute(EndpointDefinition endpoint)
    {
        var text = new StringBuilder();

        void Line(string kind, params string?[] parts)
        {
            text.Append(kind);
            foreach (var part in parts)
            {
                text.Append('|').Append(Escape(part));
            }

            text.Append('\n');
        }

        Line("endpoint", endpoint.Id, endpoint.ControllerName, endpoint.ActionName, endpoint.HttpMethod.ToString(), endpoint.RouteTemplate);

        foreach (var parameter in endpoint.Parameters)
        {
            Line("param", parameter.Name, parameter.Location.ToString(), parameter.TypeName, Flag(parameter.IsRequired), parameter.DefaultValue);
        }

        if (endpoint.RequestBody is { } body)
        {
            Line("body", body.TypeName, body.ContentType, Flag(body.IsRequired));
            Properties(body.Properties, 0);
            Example("bodyExample", body.Example);
        }

        foreach (var response in endpoint.Responses)
        {
            Line("response", response.StatusCode.ToString(), response.TypeName, response.ContentType);
            Example("responseExample", response.Example);
        }

        var auth = endpoint.Authentication;
        Line("auth", Flag(auth.RequiresAuthentication), auth.Scheme, string.Join(',', auth.Roles), string.Join(',', auth.Policies));

        foreach (var rule in endpoint.Validation)
        {
            Line("rule", rule.PropertyName, rule.RuleName, string.Join(',', rule.Arguments));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();

        void Properties(IReadOnlyList<PropertyDefinition> properties, int depth)
        {
            foreach (var property in properties)
            {
                Line(
                    "prop", depth.ToString(), property.Name, property.TypeName, Flag(property.IsRequired), Flag(property.IsNullable),
                    property.JsonName, property.Example?.Json, property.DefaultValue?.Json);
                Properties(property.Properties, depth + 1);
            }
        }

        void Example(string kind, ExampleDefinition? example)
        {
            if (example is not null)
            {
                Line(kind, example.Json.Replace("\r\n", "\n", StringComparison.Ordinal), Flag(example.IsComplete), string.Join(',', example.MissingFields));
            }
        }
    }

    private static string Flag(bool value) => value ? "1" : "0";

    private static string Escape(string? value) => value is null
        ? "\\0"
        : value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal)
               .Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
}
