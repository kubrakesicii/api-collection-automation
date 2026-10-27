using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ApiCollection.Analysis.Discovery;
using ApiCollection.Core.Models;

namespace ApiCollection.Cli;

/// <summary>Renders a discovery result as a human-readable text report or as a JSON document. Output is deterministic (LF, no timestamps).</summary>
internal static class DiscoveryReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string ToJson(EndpointDiscoveryResult result)
    {
        var report = new
        {
            summary = new
            {
                endpoints = result.Endpoints.Count,
                controllers = result.Endpoints.Select(e => e.ControllerName).Distinct().Count(),
                warnings = result.Warnings.Count,
                exampleDiagnostics = result.ExampleDiagnostics.Count
            },
            endpoints = result.Endpoints,
            warnings = result.Warnings,
            exampleDiagnostics = result.ExampleDiagnostics
        };

        return JsonSerializer.Serialize(report, JsonOptions).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    public static string ToText(EndpointDiscoveryResult result)
    {
        var text = new StringBuilder();
        void Line(string value = "") => text.Append(value).Append('\n');

        foreach (var endpoint in result.Endpoints)
        {
            Line($"{endpoint.HttpMethod.ToString().ToUpperInvariant(),-7} /{endpoint.RouteTemplate,-28} {endpoint.ControllerName}.{endpoint.ActionName}");

            foreach (var parameter in endpoint.Parameters.Where(p => p.Location != ParameterLocation.Body))
            {
                Line($"          {parameter.Location.ToString().ToLowerInvariant()} {parameter.Name}: {parameter.TypeName}{(parameter.IsRequired ? " (required)" : "")}");
            }

            if (endpoint.RequestBody is { } body)
            {
                Line($"          body {body.TypeName}{(body.IsRequired ? " (required)" : "")}");
                Properties(body.Properties, "            ");
                Example("request example", body.Example);
            }

            var auth = endpoint.Authentication;
            if (auth.RequiresAuthentication)
            {
                var details = string.Join("; ", new[]
                {
                    auth.Scheme is null ? null : $"scheme {auth.Scheme}",
                    auth.Roles.Count == 0 ? null : $"roles {string.Join(",", auth.Roles)}",
                    auth.Policies.Count == 0 ? null : $"policies {string.Join(",", auth.Policies)}"
                }.Where(d => d is not null));
                Line($"          auth: required{(details.Length == 0 ? "" : " (" + details + ")")}");
            }

            foreach (var rule in endpoint.Validation)
            {
                Line($"          validation: {rule.PropertyName} {rule.RuleName}({string.Join(", ", rule.Arguments)})");
            }

            foreach (var response in endpoint.Responses.Where(r => r.Example is not null))
            {
                Example($"response example {response.StatusCode}", response.Example);
            }
        }

        Line($"{result.Endpoints.Count} endpoint(s), {result.Warnings.Count} warning(s), {result.ExampleDiagnostics.Count} example diagnostic(s)");
        foreach (var warning in result.Warnings)
        {
            Line($"WARN {warning}");
        }

        foreach (var diagnostic in result.ExampleDiagnostics)
        {
            Line($"{diagnostic.Code} {diagnostic.Endpoint ?? "-"} [{diagnostic.Target}] {diagnostic.Message}");
        }

        return text.ToString();

        void Properties(IReadOnlyList<PropertyDefinition> properties, string indent)
        {
            foreach (var property in properties)
            {
                Line($"{indent}{property.Name}: {property.TypeName}{(property.IsRequired ? " (required)" : "")}");
                Properties(property.Properties, indent + "  ");
            }
        }

        void Example(string label, ExampleDefinition? example)
        {
            if (example is null)
            {
                return;
            }

            var state = example.IsComplete ? "complete" : $"incomplete, missing: {string.Join(", ", example.MissingFields)}";
            Line($"          {label} ({state}):");
            Line($"            {JsonNode.Parse(example.Json)?.ToJsonString()}");
            foreach (var field in example.Fields)
            {
                Line($"            source {field.Path} <- {field.Source} [{field.SourceDetail}]");
            }
        }
    }
}
