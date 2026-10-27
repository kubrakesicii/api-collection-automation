using ApiCollection.Analysis.Discovery;
using ApiCollection.Core.Models;

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

    [Fact]
    public void Sample_api_parameters_and_request_bodies_are_extracted()
    {
        var result = new EndpointDiscoveryEngine().DiscoverFromDirectory(SampleApiDirectory());

        var getById = result.Endpoints.Single(e => e.ControllerName == "Products" && e.ActionName == "GetById");
        var id = Assert.Single(getById.Parameters);
        Assert.Equal(("id", ParameterLocation.Path, "int", true), (id.Name, id.Location, id.TypeName, id.IsRequired));
        Assert.Null(getById.RequestBody);

        var create = result.Endpoints.Single(e => e.ControllerName == "Products" && e.ActionName == "Create");
        Assert.Equal("CreateProductRequest", create.RequestBody!.TypeName);
        Assert.Equal(
            ["Name:string:True", "Price:decimal:False"],
            create.RequestBody.Properties.Select(p => $"{p.Name}:{p.TypeName}:{p.IsRequired}"));

        var update = result.Endpoints.Single(e => e.ControllerName == "Orders" && e.ActionName == "Update");
        Assert.Equal(["id", "request"], update.Parameters.Select(p => p.Name));
        Assert.Equal("UpdateOrderRequest", update.RequestBody!.TypeName);

        // SampleApi has no [Authorize] and no FluentValidation validators, so nothing may be reported for them.
        Assert.All(result.Endpoints, e =>
        {
            Assert.False(e.Authentication.RequiresAuthentication);
            Assert.Empty(e.Validation);
        });
    }
}
