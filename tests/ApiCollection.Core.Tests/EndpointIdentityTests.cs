using ApiCollection.Core.Identity;
using ApiCollection.Core.Models;

namespace ApiCollection.Core.Tests;

public sealed class EndpointIdentityTests
{
    [Theory]
    [InlineData("api/Products", "GET /api/products")]
    [InlineData("API/Products/", "GET /api/products")]
    [InlineData("/api//Products", "GET /api/products")]
    [InlineData("~/Absolute/Path", "GET /absolute/path")]
    [InlineData("", "GET /")]
    [InlineData("/", "GET /")]
    [InlineData("api/Products/{id}", "GET /api/products/{}")]
    [InlineData("api/Products/{productId}", "GET /api/products/{}")]
    [InlineData("api/Products/{id:int}", "GET /api/products/{:int}")]
    [InlineData("api/Products/{id:INT}", "GET /api/products/{:int}")]
    [InlineData("api/Products/{id:int:min(1)}", "GET /api/products/{:int:min(1)}")]
    [InlineData("api/Products/{id:int=5}", "GET /api/products/{:int}")]
    [InlineData("api/Products/{id=5}", "GET /api/products/{}")]
    [InlineData("api/Products/{name?}", "GET /api/products/{?}")]
    [InlineData("api/Products/{id:int?}", "GET /api/products/{:int?}")]
    [InlineData("files/{*rest}", "GET /files/{*}")]
    [InlineData("files/{**path}", "GET /files/{**}")]
    [InlineData("a/{x:int}/b/{y:guid}", "GET /a/{:int}/b/{:guid}")]
    public void Route_is_canonicalized_to_its_shape(string route, string expected) =>
        Assert.Equal(expected, EndpointIdentity.Create(HttpMethodKind.Get, route));

    [Fact]
    public void Regex_constraints_keep_their_case_arguments_and_nested_braces()
    {
        Assert.Equal(
            "GET /codes/{:regex(^[A-Z]{3}$)}",
            EndpointIdentity.Create(HttpMethodKind.Get, "Codes/{code:Regex(^[A-Z]{3}$)}"));
        Assert.Equal(
            "GET /codes/{:regex(a/b)}/x",
            EndpointIdentity.Create(HttpMethodKind.Get, "codes/{code:REGEX(a/b)}/x"));
    }

    [Theory]
    [InlineData(HttpMethodKind.Post, "POST /api/products")]
    [InlineData(HttpMethodKind.Delete, "DELETE /api/products")]
    [InlineData(HttpMethodKind.Patch, "PATCH /api/products")]
    public void Method_is_part_of_the_identity(HttpMethodKind method, string expected) =>
        Assert.Equal(expected, EndpointIdentity.Create(method, "api/Products"));

    // ---- what changes the identity and what does not ----

    [Fact]
    public void Cosmetic_route_changes_keep_the_identity()
    {
        var baseline = EndpointIdentity.Create(HttpMethodKind.Get, "api/Products/{id:int}");

        Assert.Equal(baseline, EndpointIdentity.Create(HttpMethodKind.Get, "API/products/{id:int}"));
        Assert.Equal(baseline, EndpointIdentity.Create(HttpMethodKind.Get, "/api/Products/{id:int}/"));
        Assert.Equal(baseline, EndpointIdentity.Create(HttpMethodKind.Get, "~/api/Products/{id:int}"));
        Assert.Equal(baseline, EndpointIdentity.Create(HttpMethodKind.Get, "api/Products/{productId:int}"));
        Assert.Equal(baseline, EndpointIdentity.Create(HttpMethodKind.Get, "api/Products/{id:INT}"));
    }

    [Fact]
    public void Real_route_changes_produce_a_different_identity()
    {
        var baseline = EndpointIdentity.Create(HttpMethodKind.Get, "api/Products/{id:int}");

        Assert.NotEqual(baseline, EndpointIdentity.Create(HttpMethodKind.Post, "api/Products/{id:int}"));
        Assert.NotEqual(baseline, EndpointIdentity.Create(HttpMethodKind.Get, "api/Items/{id:int}"));
        Assert.NotEqual(baseline, EndpointIdentity.Create(HttpMethodKind.Get, "api/Products/{id:guid}"));
        Assert.NotEqual(baseline, EndpointIdentity.Create(HttpMethodKind.Get, "api/Products/{id}"));
        Assert.NotEqual(baseline, EndpointIdentity.Create(HttpMethodKind.Get, "api/Products/{id:int?}"));
        Assert.NotEqual(baseline, EndpointIdentity.Create(HttpMethodKind.Get, "api/Products/{id:int}/details"));
    }

    [Fact]
    public void Constraint_overloads_stay_distinct()
    {
        Assert.NotEqual(
            EndpointIdentity.Create(HttpMethodKind.Get, "api/x/{id:int}"),
            EndpointIdentity.Create(HttpMethodKind.Get, "api/x/{name}"));
    }

    // ---- canonicalizing human-written identities ----

    [Theory]
    [InlineData("get api/Products/{id:int}", "GET /api/products/{:int}")]
    [InlineData("GET /api/products/{:int}", "GET /api/products/{:int}")]
    [InlineData("  Post   /API/Products  ", "POST /api/products")]
    [InlineData("DELETE api/Products/{id:int}", "DELETE /api/products/{:int}")]
    public void Human_written_identities_are_canonicalized(string text, string expected) =>
        Assert.Equal(expected, EndpointIdentity.Canonicalize(text));

    [Theory]
    [InlineData("api/Products")]
    [InlineData("FETCH api/Products")]
    [InlineData("1 api/Products")]
    [InlineData("")]
    public void Text_without_an_http_method_is_returned_unchanged(string text) =>
        Assert.Equal(text.Trim(), EndpointIdentity.Canonicalize(text));

    [Theory]
    [InlineData("api/Products")]
    [InlineData("api/Products/{id:int}")]
    [InlineData("files/{**path}")]
    [InlineData("a/{x:int?}/b/{y:regex(^[a-z]{2}$)}")]
    [InlineData("api/Products/{name?}")]
    [InlineData("")]
    public void Canonicalizing_a_canonical_identity_changes_nothing(string route)
    {
        var identity = EndpointIdentity.Create(HttpMethodKind.Put, route);

        Assert.Equal(identity, EndpointIdentity.Canonicalize(identity));
        Assert.Equal(EndpointIdentity.CanonicalRoute(route), EndpointIdentity.CanonicalRoute(EndpointIdentity.CanonicalRoute(route)));
    }

    [Fact]
    public void Malformed_templates_do_not_throw()
    {
        Assert.Equal("GET /a/{id:int", EndpointIdentity.Create(HttpMethodKind.Get, "A/{id:int"));
        Assert.Equal("GET /a/}", EndpointIdentity.Create(HttpMethodKind.Get, "A/}"));
    }
}
