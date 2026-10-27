using ApiCollection.Analysis.Discovery;

namespace ApiCollection.Analysis.Tests;

public sealed class SampleApiDiscoveryTests
{
    private static string SampleApiDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ApiCollection.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "sample-api", "SampleApi");
    }

    [Fact]
    public void All_supported_sample_api_endpoints_are_discovered()
    {
        var result = new EndpointDiscoveryEngine().DiscoverFromDirectory(SampleApiDirectory());

        var actual = result.Endpoints
            .Select(e => $"{e.HttpMethod} {e.RouteTemplate} {e.ControllerName}.{e.ActionName}")
            .ToList();

        List<string> expected = [];
        foreach (var controller in new[] { "Customers", "Orders", "Products" })
        {
            expected.Add($"Get api/{controller} {controller}.GetAll");
            expected.Add($"Post api/{controller} {controller}.Create");
            expected.Add($"Get api/{controller}/{{id:int}} {controller}.GetById");
            expected.Add($"Put api/{controller}/{{id:int}} {controller}.Update");
            expected.Add($"Delete api/{controller}/{{id:int}} {controller}.Delete");
        }

        Assert.Equal(expected.OrderBy(s => s, StringComparer.Ordinal), actual.OrderBy(s => s, StringComparer.Ordinal));
        Assert.Empty(result.Warnings);
        Assert.Equal(actual.Count, result.Endpoints.Select(e => e.Id).Distinct().Count());
    }
}
