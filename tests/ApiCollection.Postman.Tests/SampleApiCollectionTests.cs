using System.Text.Json;
using ApiCollection.Analysis.Discovery;
using ApiCollection.Infrastructure.Configuration;
using ApiCollection.Postman.Generation;
using ApiCollection.Postman.Serialization;

namespace ApiCollection.Postman.Tests;

public sealed class SampleApiCollectionTests
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
    public void Every_sample_api_endpoint_is_generated_in_its_controller_folder_with_correct_http_info()
    {
        var root = RepositoryRoot();
        var metadata = ExampleMetadataLoader.Load(Path.Combine(root, "config", "examples.sample-api.json"));
        var discovery = new EndpointDiscoveryEngine().DiscoverFromDirectory(Path.Combine(root, "sample-api", "SampleApi"), metadata);

        var json = PostmanCollectionSerializer.Serialize(new PostmanCollectionGenerator().Generate(discovery.Endpoints));

        using var document = JsonDocument.Parse(json);
        var actual = document.RootElement.GetProperty("item").EnumerateArray()
            .SelectMany(folder => folder.GetProperty("item").EnumerateArray().Select(item => (
                Folder: folder.GetProperty("name").GetString(),
                Name: item.GetProperty("name").GetString(),
                Method: item.GetProperty("request").GetProperty("method").GetString(),
                Url: item.GetProperty("request").GetProperty("url").GetProperty("raw").GetString())))
            .ToList();

        List<(string?, string?, string?, string?)> expected = [];
        foreach (var controller in new[] { "Customers", "Orders", "Products" })
        {
            expected.Add((controller, "GetAll", "GET", $"{{{{baseUrl}}}}/api/{controller}"));
            expected.Add((controller, "Create", "POST", $"{{{{baseUrl}}}}/api/{controller}"));
            expected.Add((controller, "GetById", "GET", $"{{{{baseUrl}}}}/api/{controller}/:id"));
            expected.Add((controller, "Update", "PUT", $"{{{{baseUrl}}}}/api/{controller}/:id"));
            expected.Add((controller, "Delete", "DELETE", $"{{{{baseUrl}}}}/api/{controller}/:id"));
        }

        Assert.Equal(15, actual.Count);
        Assert.Equal(
            expected.OrderBy(e => e.ToString(), StringComparer.Ordinal),
            actual.Select(a => ((string?, string?, string?, string?))(a.Folder, a.Name, a.Method, a.Url)).OrderBy(e => e.ToString(), StringComparer.Ordinal));
        Assert.Equal(["Customers", "Orders", "Products"], document.RootElement.GetProperty("item").EnumerateArray().Select(f => f.GetProperty("name").GetString()));
    }

    [Fact]
    public void Sample_api_bodies_use_curated_examples_and_no_token_value_is_written()
    {
        var root = RepositoryRoot();
        var metadata = ExampleMetadataLoader.Load(Path.Combine(root, "config", "examples.sample-api.json"));
        var discovery = new EndpointDiscoveryEngine().DiscoverFromDirectory(Path.Combine(root, "sample-api", "SampleApi"), metadata);

        var collection = new PostmanCollectionGenerator().Generate(discovery.Endpoints);
        var create = collection.Items.Single(f => f.Name == "Products").Items.Single(i => i.Name == "Create").Request;

        Assert.Equal("""{"name":"Laptop","price":1299.9}""", JsonDocument.Parse(create.Body!.Raw).RootElement.GetRawText().Replace(" ", "").Replace("\n", ""));
        Assert.Null(create.Auth);
        Assert.Equal("", collection.Variables.Single(v => v.Key == "token").Value);
        Assert.Empty(collection.Items.SelectMany(f => f.Items).SelectMany(i => i.Response));

        // Without curated examples nothing is invented: bodies fall back to labelled placeholder skeletons.
        var bare = new PostmanCollectionGenerator().Generate(new EndpointDiscoveryEngine().DiscoverFromDirectory(Path.Combine(root, "sample-api", "SampleApi")).Endpoints);
        var skeleton = bare.Items.Single(f => f.Name == "Products").Items.Single(i => i.Name == "Create").Request;
        Assert.Contains("<string>", skeleton.Body!.Raw);
        Assert.Contains("placeholders, not real examples", skeleton.Description);
    }
}
