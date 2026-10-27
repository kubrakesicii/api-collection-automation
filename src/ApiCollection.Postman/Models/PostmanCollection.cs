using System.Text.Json.Serialization;

namespace ApiCollection.Postman.Models;

/// <summary>Postman Collection v2.1 document (only the parts the generator produces).</summary>
public sealed class PostmanCollection
{
    [JsonPropertyName("info")]
    public required PostmanInfo Info { get; init; }

    [JsonPropertyName("item")]
    public IReadOnlyList<PostmanFolder> Items { get; init; } = [];

    [JsonPropertyName("variable")]
    public IReadOnlyList<PostmanVariable> Variables { get; init; } = [];
}

public sealed class PostmanInfo
{
    public const string SchemaV21 = "https://schema.getpostman.com/json/collection/v2.1.0/collection.json";

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("schema")]
    public string Schema { get; init; } = SchemaV21;
}

public sealed class PostmanFolder
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("item")]
    public IReadOnlyList<PostmanRequestItem> Items { get; init; } = [];
}

public sealed class PostmanRequestItem
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("request")]
    public required PostmanRequest Request { get; init; }

    /// <summary>Saved example responses. Always empty: responses are never fabricated.</summary>
    [JsonPropertyName("response")]
    public IReadOnlyList<object> Response { get; init; } = [];
}

public sealed class PostmanRequest
{
    [JsonPropertyName("method")]
    public required string Method { get; init; }

    [JsonPropertyName("header")]
    public IReadOnlyList<PostmanHeader> Headers { get; init; } = [];

    [JsonPropertyName("url")]
    public required PostmanUrl Url { get; init; }

    [JsonPropertyName("body")]
    public PostmanBody? Body { get; init; }

    [JsonPropertyName("auth")]
    public PostmanAuth? Auth { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

public sealed class PostmanUrl
{
    [JsonPropertyName("raw")]
    public required string Raw { get; init; }

    [JsonPropertyName("host")]
    public IReadOnlyList<string> Host { get; init; } = [];

    [JsonPropertyName("path")]
    public IReadOnlyList<string> Path { get; init; } = [];

    [JsonPropertyName("query")]
    public IReadOnlyList<PostmanQueryParameter>? Query { get; init; }

    /// <summary>Path variables (the <c>:name</c> segments of <see cref="Path"/>).</summary>
    [JsonPropertyName("variable")]
    public IReadOnlyList<PostmanVariable>? Variable { get; init; }
}

public sealed class PostmanQueryParameter
{
    [JsonPropertyName("key")]
    public required string Key { get; init; }

    [JsonPropertyName("value")]
    public string Value { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("disabled")]
    public bool? Disabled { get; init; }
}

public sealed class PostmanHeader
{
    [JsonPropertyName("key")]
    public required string Key { get; init; }

    [JsonPropertyName("value")]
    public string Value { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = "text";

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("disabled")]
    public bool? Disabled { get; init; }
}

public sealed class PostmanVariable
{
    [JsonPropertyName("key")]
    public required string Key { get; init; }

    [JsonPropertyName("value")]
    public string Value { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

public sealed class PostmanBody
{
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = "raw";

    [JsonPropertyName("raw")]
    public required string Raw { get; init; }

    [JsonPropertyName("options")]
    public PostmanBodyOptions Options { get; init; } = new();
}

public sealed class PostmanBodyOptions
{
    [JsonPropertyName("raw")]
    public PostmanRawOptions Raw { get; init; } = new();
}

public sealed class PostmanRawOptions
{
    [JsonPropertyName("language")]
    public string Language { get; init; } = "json";
}

public sealed class PostmanAuth
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "bearer";

    [JsonPropertyName("bearer")]
    public IReadOnlyList<PostmanAuthParameter> Bearer { get; init; } = [];
}

public sealed class PostmanAuthParameter
{
    [JsonPropertyName("key")]
    public required string Key { get; init; }

    [JsonPropertyName("value")]
    public required string Value { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = "string";
}
