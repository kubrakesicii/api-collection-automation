using System.Text.Json;
using ApiCollection.Core.Models;

namespace ApiCollection.Infrastructure.Configuration;

/// <summary>
/// Reads curated example metadata from a JSON file:
/// <code>
/// {
///   "properties": { "CreateProductRequest.Name": "Laptop" },
///   "requests":   { "POST api/Products": { "name": "Laptop", "price": 10 } },
///   "responses":  { "GET api/Products/{id:int}": { "200": { "id": 1 } } }
/// }
/// </code>
/// The file only holds example values; it must never contain secrets or real customer data.
/// </summary>
public static class ExampleMetadataLoader
{
    private static readonly JsonDocumentOptions Options = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public static ExampleMetadata Load(string path) => Parse(File.ReadAllText(path), path);

    public static ExampleMetadata Parse(string json, string sourceName)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, Options);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{sourceName}: invalid JSON ({exception.Message})", exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"{sourceName}: the root must be a JSON object.");
            }

            var properties = new Dictionary<string, string>();
            var requests = new Dictionary<string, string>();
            var responses = new Dictionary<string, IReadOnlyDictionary<int, string>>();

            foreach (var section in root.EnumerateObject())
            {
                switch (section.Name)
                {
                    case "properties":
                        Fill(section.Value, properties, sourceName, section.Name);
                        break;
                    case "requests":
                        Fill(section.Value, requests, sourceName, section.Name);
                        break;
                    case "responses":
                        FillResponses(section.Value, responses, sourceName);
                        break;
                    default:
                        throw new InvalidDataException($"{sourceName}: unknown section '{section.Name}' (expected properties, requests, responses).");
                }
            }

            return new ExampleMetadata
            {
                SourceName = sourceName,
                PropertyExamples = properties,
                RequestExamples = requests,
                ResponseExamples = responses
            };
        }
    }

    private static void Fill(JsonElement element, Dictionary<string, string> target, string sourceName, string section)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{sourceName}: '{section}' must be a JSON object.");
        }

        foreach (var entry in element.EnumerateObject())
        {
            target[entry.Name] = entry.Value.GetRawText();
        }
    }

    private static void FillResponses(JsonElement element, Dictionary<string, IReadOnlyDictionary<int, string>> target, string sourceName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{sourceName}: 'responses' must be a JSON object.");
        }

        foreach (var endpoint in element.EnumerateObject())
        {
            if (endpoint.Value.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"{sourceName}: responses['{endpoint.Name}'] must map status codes to examples.");
            }

            var byStatus = new Dictionary<int, string>();
            foreach (var status in endpoint.Value.EnumerateObject())
            {
                if (!int.TryParse(status.Name, out var code))
                {
                    throw new InvalidDataException($"{sourceName}: responses['{endpoint.Name}'] has a non-numeric status code '{status.Name}'.");
                }

                byStatus[code] = status.Value.GetRawText();
            }

            target[endpoint.Name] = byStatus;
        }
    }
}
