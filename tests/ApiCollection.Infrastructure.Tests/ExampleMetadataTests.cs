using ApiCollection.Analysis.Discovery;
using ApiCollection.Core.Models;
using ApiCollection.Infrastructure.Configuration;

namespace ApiCollection.Infrastructure.Tests;

public sealed class ExampleMetadataTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ApiCollection.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }

    [Fact]
    public void Loader_reads_properties_requests_and_responses_as_raw_json()
    {
        var metadata = ExampleMetadataLoader.Parse("""
            {
              // comments and trailing commas are tolerated
              "properties": { "A.B": "text", "A.C": 1.5, },
              "requests": { "POST api/A": { "b": "x" } },
              "responses": { "GET api/A/{id}": { "200": { "id": 1 }, "404": null } }
            }
            """, "examples.json");

        Assert.Equal("examples.json", metadata.SourceName);
        Assert.Equal("\"text\"", metadata.PropertyExamples["A.B"]);
        Assert.Equal("1.5", metadata.PropertyExamples["A.C"]);
        Assert.Equal("""{ "b": "x" }""", metadata.RequestExamples["POST api/A"]);
        Assert.Equal([200, 404], metadata.ResponseExamples["GET api/A/{id}"].Keys.Order());
        Assert.Equal("null", metadata.ResponseExamples["GET api/A/{id}"][404]);
    }

    [Theory]
    [InlineData("{ not json", "invalid JSON")]
    [InlineData("[]", "root must be a JSON object")]
    [InlineData("""{ "secrets": {} }""", "unknown section 'secrets'")]
    [InlineData("""{ "properties": [] }""", "'properties' must be a JSON object")]
    [InlineData("""{ "responses": { "GET a": { "ok": {} } } }""", "non-numeric status code 'ok'")]
    [InlineData("""{ "responses": { "GET a": 5 } }""", "must map status codes")]
    public void Loader_rejects_malformed_files_with_a_clear_message(string json, string expected)
    {
        var exception = Assert.Throws<InvalidDataException>(() => ExampleMetadataLoader.Parse(json, "bad.json"));

        Assert.Contains("bad.json", exception.Message);
        Assert.Contains(expected, exception.Message);
    }

    [Fact]
    public void Sample_api_examples_come_only_from_the_curated_metadata_file()
    {
        var root = RepositoryRoot();
        var sampleApi = Path.Combine(root, "sample-api", "SampleApi");
        var engine = new EndpointDiscoveryEngine();

        // Without metadata nothing may be invented: the sample DTOs carry no static examples.
        var bare = engine.DiscoverFromDirectory(sampleApi);
        Assert.All(bare.Endpoints, e =>
        {
            Assert.Null(e.RequestBody?.Example);
            Assert.Empty(e.Responses);
        });
        Assert.Equal(
            bare.Endpoints.Count(e => e.RequestBody is not null),
            bare.ExampleDiagnostics.Count(d => d.Code == "EX001"));
        Assert.Equal(6, bare.Endpoints.Count(e => e.RequestBody is not null));

        var metadata = ExampleMetadataLoader.Load(Path.Combine(root, "config", "examples.sample-api.json"));
        var result = engine.DiscoverFromDirectory(sampleApi, metadata);

        var create = result.Endpoints.Single(e => e.ControllerName == "Products" && e.ActionName == "Create");
        Assert.Equal("""{"name":"Laptop","price":1299.9}""", System.Text.Json.Nodes.JsonNode.Parse(create.RequestBody!.Example!.Json)!.ToJsonString());
        Assert.True(create.RequestBody.Example.IsComplete);
        Assert.All(create.RequestBody.Example.Fields, f => Assert.Equal(ExampleSource.ExternalMetadata, f.Source));

        var getById = result.Endpoints.Single(e => e.ControllerName == "Products" && e.ActionName == "GetById");
        var response = Assert.Single(getById.Responses);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal(ExampleSource.ExternalMetadata, Assert.Single(response.Example!.Fields).Source);

        // Every request body of the sample API is covered; nothing in the metadata file is stale.
        Assert.All(result.Endpoints.Where(e => e.RequestBody is not null), e => Assert.True(e.RequestBody!.Example!.IsComplete));
        Assert.DoesNotContain(result.ExampleDiagnostics, d => d.Code is "EX001" or "EX002" or "EX003" or "EX005");
    }
}
