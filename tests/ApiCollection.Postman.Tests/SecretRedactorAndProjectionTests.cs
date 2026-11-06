using System.Text.Json.Nodes;
using ApiCollection.Analysis.Discovery;
using ApiCollection.Postman.Generation;
using ApiCollection.Postman.Serialization;
using ApiCollection.Postman.Sync;

namespace ApiCollection.Postman.Tests;

public sealed class SecretRedactorAndProjectionTests
{
    private const string Token = "eyJhbGciOiJIUzI1NiJ9.super-secret-token-value";
    private const string Password = "hunter2-very-secret";

    private static readonly string WithSecrets = """
        {
          "info": { "name": "Secrets" },
          "auth": { "type": "bearer", "bearer": [ { "key": "token", "value": "@@TOKEN@@", "type": "string" } ] },
          "variable": [
            { "key": "baseUrl", "value": "https://api.example.com" },
            { "key": "token", "value": "@@TOKEN@@" },
            { "key": "refreshToken", "value": "{{refresh}}" },
            { "key": "apiKey", "value": "" },
            { "key": "dbPassword", "value": "@@PASSWORD@@" }
          ],
          "item": [
            { "name": "Folder", "item": [
              { "name": "Request",
                "request": {
                  "method": "GET",
                  "header": [
                    { "key": "Authorization", "value": "Bearer @@TOKEN@@" },
                    { "key": "Cookie", "value": "session=abc" },
                    { "key": "X-Api-Key", "value": "{{key}}" },
                    { "key": "Content-Type", "value": "application/json" }
                  ],
                  "auth": { "type": "basic", "basic": [ { "key": "username", "value": "ada" }, { "key": "password", "value": "@@PASSWORD@@" } ] },
                  "url": { "raw": "{{baseUrl}}/x" }
                } },
              { "name": "Keyed", "request": { "method": "GET", "auth": { "type": "apikey", "apikey": [ { "key": "key", "value": "X-Key" }, { "key": "value", "value": "@@TOKEN@@" } ] } } }
            ] }
          ]
        }
        """.Replace("@@TOKEN@@", Token).Replace("@@PASSWORD@@", Password);

    // ---- redaction ----

    [Fact]
    public void Secret_looking_values_are_replaced_and_everything_else_is_kept()
    {
        var (json, paths) = SecretRedactor.Redact(WithSecrets);

        Assert.DoesNotContain(Token, json);
        Assert.DoesNotContain(Password, json);
        Assert.DoesNotContain("session=abc", json);
        var collection = JsonNode.Parse(json)!;
        Assert.Equal("https://api.example.com", (string?)collection["variable"]!.AsArray().Single(v => (string?)v!["key"] == "baseUrl")!["value"]);
        Assert.Equal("ada", (string?)collection["item"]![0]!["item"]![0]!["request"]!["auth"]!["basic"]![0]!["value"]);
        Assert.Equal("application/json", (string?)collection["item"]![0]!["item"]![0]!["request"]!["header"]![3]!["value"]);
        Assert.Equal("X-Key", (string?)collection["item"]![0]!["item"]![1]!["request"]!["auth"]!["apikey"]![0]!["value"]);
        Assert.Equal(
            new[]
            {
                "auth.bearer[token]",
                "variable[token]",
                "variable[dbPassword]",
                "item[Folder/Request].request.header[Authorization]",
                "item[Folder/Request].request.header[Cookie]",
                "item[Folder/Request].request.auth.basic[password]",
                "item[Folder/Keyed].request.auth.apikey[value]"
            }.Order(StringComparer.Ordinal),
            paths.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Variable_references_and_empty_values_are_not_secrets()
    {
        var (json, paths) = SecretRedactor.Redact(WithSecrets);
        var collection = JsonNode.Parse(json)!;

        Assert.Equal("{{refresh}}", (string?)collection["variable"]!.AsArray().Single(v => (string?)v!["key"] == "refreshToken")!["value"]);
        Assert.Equal("", (string?)collection["variable"]!.AsArray().Single(v => (string?)v!["key"] == "apiKey")!["value"]);
        Assert.Equal("{{key}}", (string?)collection["item"]![0]!["item"]![0]!["request"]!["header"]![2]!["value"]);
        Assert.DoesNotContain(paths, p => p.Contains("refreshToken") || p.Contains("apiKey") || p.Contains("X-Api-Key"));
    }

    [Fact]
    public void The_redaction_report_names_places_and_never_values()
    {
        var (json, paths) = SecretRedactor.Redact(WithSecrets);

        Assert.All(paths, p => Assert.DoesNotContain(Token, p));
        Assert.All(paths, p => Assert.DoesNotContain(Password, p));
        Assert.Contains(SecretRedactor.Placeholder, json);
    }

    [Fact]
    public void A_collection_generated_by_this_tool_has_nothing_to_redact_so_a_backup_of_it_is_exact()
    {
        var generated = PostmanCollectionSerializer.Serialize(new PostmanCollectionGenerator().Generate(
            new EndpointDiscoveryEngine().DiscoverFromSources(["""
                [ApiController][Route("api/[controller]")]
                public class ItemsController : ControllerBase { [Authorize] [HttpGet] public IActionResult List() => Ok(); }
                """]).Endpoints,
            new PostmanCollectionOptions { Name = "Generated", IdNamespace = "ns" }));

        var (json, paths) = SecretRedactor.Redact(generated);

        Assert.Empty(paths);
        Assert.Equal(JsonNode.Parse(generated)!.ToJsonString(), json);
    }

    [Fact]
    public void Redaction_changes_only_the_redacted_facts_and_leaves_the_structure_comparable()
    {
        var (json, _) = SecretRedactor.Redact(WithSecrets);

        var differences = CollectionProjection.Differences(CollectionProjection.Of(WithSecrets), CollectionProjection.Of(json));

        Assert.Equal(["changed request[root/Folder/Request].headers", "changed variable[dbPassword]", "changed variable[token]"], differences);
    }

    // ---- projection ----

    private static readonly string Remote = """
        {
          "info": { "_postman_id": "p1", "name": "Api", "description": { "content": "About\r\n", "type": "text/plain" },
                    "updatedAt": "2026-01-01T00:00:00Z", "uid": "1-p1", "schema": "https://schema.postman.com/json/collection/v2.1.0/collection.json" },
          "variable": [ { "key": "baseUrl", "value": "http://localhost:5000" } ],
          "item": [
            { "id": "f1", "uid": "1-f1", "name": "Items", "item": [
              { "id": "r1", "uid": "1-r1", "name": "List",
                "request": { "method": "get", "url": { "raw": "{{baseUrl}}/api/items", "host": ["{{baseUrl}}"] },
                             "description": { "content": "Lists items.", "type": "text/markdown" },
                             "header": [ { "key": "B", "value": "2" }, { "key": "A", "value": "1", "disabled": true } ],
                             "auth": { "type": "bearer", "bearer": [ { "key": "token", "value": "{{token}}" } ] } },
                "response": [] },
              { "id": "r2", "name": "Create",
                "request": { "method": "POST", "url": "{{baseUrl}}/api/items", "body": { "mode": "raw", "raw": "{\r\n  \"a\": 1\r\n}" } } }
            ] }
          ]
        }
        """;

    [Fact]
    public void Facts_describe_names_methods_urls_bodies_headers_and_auth_keyed_by_item_id()
    {
        var facts = CollectionProjection.Of(Remote);

        Assert.Equal("Api", facts["collection.name"]);
        Assert.Equal("About", facts["collection.description"]);
        Assert.Equal("http://localhost:5000", facts["variable[baseUrl]"]);
        Assert.Equal("Items", facts["folder[f1].name"]);
        Assert.Equal("f1", facts["request[r1].parent"]);
        Assert.Equal("GET", facts["request[r1].method"]);
        Assert.Equal("{{baseUrl}}/api/items", facts["request[r1].url"]);
        Assert.Equal("Lists items.", facts["request[r1].description"]);
        Assert.Equal("A=1|disabled;B=2", facts["request[r1].headers"]);
        Assert.Equal("bearer", facts["request[r1].auth"]);
        Assert.Equal("{{token}}", facts["request[r1].authToken"]);
        Assert.Equal("{{baseUrl}}/api/items", facts["request[r2].url"]);
        Assert.Equal("{\n  \"a\": 1\n}", facts["request[r2].body"]);
    }

    [Fact]
    public void Fields_postman_adds_itself_do_not_count_as_differences()
    {
        const string ours = """
            {
              "info": { "name": "Api", "description": "About", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "variable": [ { "key": "baseUrl", "value": "http://localhost:5000" } ],
              "item": [
                { "id": "f1", "name": "Items", "item": [
                  { "id": "r1", "name": "List",
                    "request": { "method": "GET", "url": { "raw": "{{baseUrl}}/api/items" }, "description": "Lists items.",
                                 "header": [ { "key": "A", "value": "1", "disabled": true }, { "key": "B", "value": "2" } ],
                                 "auth": { "type": "bearer", "bearer": [ { "key": "token", "value": "{{token}}" } ] } }, "response": [] },
                  { "id": "r2", "name": "Create",
                    "request": { "method": "POST", "url": { "raw": "{{baseUrl}}/api/items" }, "body": { "mode": "raw", "raw": "{\n  \"a\": 1\n}" } } }
                ] }
              ]
            }
            """;

        Assert.Empty(CollectionProjection.Differences(CollectionProjection.Of(ours), CollectionProjection.Of(Remote)));
    }

    [Fact]
    public void Differences_are_listed_by_path_and_kind_and_never_with_values()
    {
        var actual = Remote
            .Replace("\"method\": \"get\"", "\"method\": \"DELETE\"")
            .Replace("\"name\": \"Create\"", "\"name\": \"Make\"")
            .Replace("{ \"key\": \"baseUrl\", \"value\": \"http://localhost:5000\" }", "{ \"key\": \"baseUrl\", \"value\": \"http://secret-host\" }, { \"key\": \"extra\", \"value\": \"x\" }");

        var differences = CollectionProjection.Differences(CollectionProjection.Of(Remote), CollectionProjection.Of(actual));

        Assert.Equal(
            ["changed request[r1].method", "changed request[r2].name", "changed variable[baseUrl]", "unexpected variable[extra]"],
            differences);
        Assert.All(differences, d => Assert.DoesNotContain("secret-host", d));
        Assert.DoesNotContain(differences, d => d.Contains("DELETE"));
    }

    [Fact]
    public void A_missing_request_is_reported_as_missing_for_each_of_its_facts_and_nothing_else()
    {
        var without = JsonNode.Parse(Remote)!;
        without["item"]![0]!["item"]!.AsArray().RemoveAt(1);

        var differences = CollectionProjection.Differences(CollectionProjection.Of(Remote), CollectionProjection.Of(without.ToJsonString()));

        Assert.Contains("missing request[r2].method", differences);
        Assert.Contains("missing request[r2].name", differences);
        Assert.All(differences, d => Assert.StartsWith("missing request[r2].", d));
    }

    [Fact]
    public void Items_without_an_id_are_still_compared_by_their_place_in_the_tree()
    {
        const string noIds = """
            { "info": { "name": "x" }, "item": [ { "name": "F", "item": [ { "name": "R", "request": { "method": "GET", "url": "u" } } ] } ] }
            """;

        var facts = CollectionProjection.Of(noIds);

        Assert.Equal("GET", facts["request[root/F/R].method"]);
        Assert.Equal("u", facts["request[root/F/R].url"]);
    }
}
