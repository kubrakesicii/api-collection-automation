using ApiCollection.Core.Identity;
using ApiCollection.Core.Models;
using ApiCollection.Core.Snapshots;

namespace ApiCollection.Core.Tests;

public sealed class SnapshotTests
{
    internal static EndpointDefinition Endpoint(
        string controller = "Products", string action = "GetById", HttpMethodKind method = HttpMethodKind.Get,
        string route = "api/Products/{id:int}", Func<EndpointDefinition, EndpointDefinition>? change = null)
    {
        var endpoint = new EndpointDefinition
        {
            Id = EndpointIdentity.Create(method, route),
            ControllerName = controller,
            ActionName = action,
            HttpMethod = method,
            RouteTemplate = route,
            Parameters = [new ParameterDefinition { Name = "id", Location = ParameterLocation.Path, TypeName = "int", IsRequired = true }],
            RequestBody = new RequestBodyDefinition
            {
                TypeName = "Dto",
                Properties =
                [
                    new PropertyDefinition
                    {
                        Name = "Name", TypeName = "string",
                        Example = new ExampleValue { Json = "\"Laptop\"", Source = ExampleSource.XmlDocExample, SourceDetail = "A.cs:10: Dto.Name" }
                    }
                ],
                Example = new ExampleDefinition
                {
                    Json = "{\r\n  \"name\": \"Laptop\"\r\n}",
                    IsComplete = true,
                    Fields = [new ExampleFieldSource { Path = "Name", Source = ExampleSource.XmlDocExample, SourceDetail = "A.cs:10: Dto.Name" }]
                }
            },
            Responses = [new ResponseDefinition { StatusCode = 200, TypeName = "Dto" }],
            Authentication = new AuthenticationDefinition { RequiresAuthentication = true, Roles = ["Admin"] },
            Validation = [new ValidationRuleDefinition { PropertyName = "Name", RuleName = "MaximumLength", Arguments = ["100"] }]
        };

        return change is null ? endpoint : change(endpoint);
    }

    // ---- fingerprint ----

    [Fact]
    public void Fingerprint_is_stable_and_hex()
    {
        var first = EndpointFingerprint.Compute(Endpoint());

        Assert.Equal(first, EndpointFingerprint.Compute(Endpoint()));
        Assert.Matches("^[0-9a-f]{64}$", first);
    }

    public static TheoryData<string> ContentChanges => new()
    {
        "controller", "action", "route", "method", "parameter", "required", "body type", "property", "property example",
        "body example", "response", "auth role", "auth scheme", "validation argument", "extra validation"
    };

    [Theory]
    [MemberData(nameof(ContentChanges))]
    public void Fingerprint_changes_when_content_that_reaches_the_collection_changes(string change)
    {
        var baseline = EndpointFingerprint.Compute(Endpoint());
        var changed = change switch
        {
            "controller" => Endpoint(controller: "Items"),
            "action" => Endpoint(action: "Fetch"),
            "route" => Endpoint(route: "api/Products/{id:guid}"),
            "method" => Endpoint(method: HttpMethodKind.Put),
            "parameter" => Endpoint(change: e => e with { Parameters = [e.Parameters[0] with { TypeName = "long" }] }),
            "required" => Endpoint(change: e => e with { Parameters = [e.Parameters[0] with { IsRequired = false }] }),
            "body type" => Endpoint(change: e => e with { RequestBody = e.RequestBody! with { TypeName = "OtherDto" } }),
            "property" => Endpoint(change: e => e with
            {
                RequestBody = e.RequestBody! with { Properties = [e.RequestBody.Properties[0] with { TypeName = "int" }] }
            }),
            "property example" => Endpoint(change: e => e with
            {
                RequestBody = e.RequestBody! with
                {
                    Properties = [e.RequestBody.Properties[0] with { Example = e.RequestBody.Properties[0].Example! with { Json = "\"Phone\"" } }]
                }
            }),
            "body example" => Endpoint(change: e => e with
            {
                RequestBody = e.RequestBody! with { Example = e.RequestBody.Example! with { Json = "{ \"name\": \"Phone\" }" } }
            }),
            "response" => Endpoint(change: e => e with { Responses = [new ResponseDefinition { StatusCode = 201, TypeName = "Dto" }] }),
            "auth role" => Endpoint(change: e => e with { Authentication = e.Authentication with { Roles = ["Editor"] } }),
            "auth scheme" => Endpoint(change: e => e with { Authentication = e.Authentication with { Scheme = "Bearer" } }),
            "validation argument" => Endpoint(change: e => e with { Validation = [e.Validation[0] with { Arguments = ["50"] }] }),
            "extra validation" => Endpoint(change: e => e with
            {
                Validation = [.. e.Validation, new ValidationRuleDefinition { PropertyName = "Name", RuleName = "NotEmpty" }]
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };

        Assert.NotEqual(baseline, EndpointFingerprint.Compute(changed));
    }

    [Fact]
    public void Fingerprint_ignores_where_an_example_came_from_and_line_endings()
    {
        var baseline = EndpointFingerprint.Compute(Endpoint());

        var moved = Endpoint(change: e => e with
        {
            RequestBody = e.RequestBody! with
            {
                Properties = [e.RequestBody.Properties[0] with { Example = e.RequestBody.Properties[0].Example! with { SourceDetail = "Other/B.cs:99: Dto.Name" } }],
                Example = e.RequestBody.Example! with
                {
                    Json = "{\n  \"name\": \"Laptop\"\n}",
                    Fields = [new ExampleFieldSource { Path = "Name", Source = ExampleSource.XmlDocExample, SourceDetail = "Other/B.cs:99: Dto.Name" }]
                }
            }
        });

        Assert.Equal(baseline, EndpointFingerprint.Compute(moved));
    }

    [Fact]
    public void Fingerprint_distinguishes_null_from_empty_and_separator_characters()
    {
        var withNull = Endpoint(change: e => e with { Parameters = [e.Parameters[0] with { DefaultValue = null }] });
        var withEmpty = Endpoint(change: e => e with { Parameters = [e.Parameters[0] with { DefaultValue = "" }] });
        var withPipe = Endpoint(change: e => e with { Parameters = [e.Parameters[0] with { Name = "a|b", TypeName = "c" }] });
        var shifted = Endpoint(change: e => e with { Parameters = [e.Parameters[0] with { Name = "a", TypeName = "b|c" }] });

        Assert.NotEqual(EndpointFingerprint.Compute(withNull), EndpointFingerprint.Compute(withEmpty));
        Assert.NotEqual(EndpointFingerprint.Compute(withPipe), EndpointFingerprint.Compute(shifted));
    }

    // ---- snapshot creation ----

    [Fact]
    public void Snapshot_is_ordered_by_identity_regardless_of_input_order()
    {
        var a = Endpoint(route: "api/A", action: "A");
        var b = Endpoint(route: "api/B", action: "B");
        var c = Endpoint(route: "api/C", method: HttpMethodKind.Post, action: "C");

        var first = AnalysisSnapshot.Create([c, a, b]);
        var second = AnalysisSnapshot.Create([b, c, a]);

        Assert.Equal(["GET /api/a", "GET /api/b", "POST /api/c"], first.Endpoints.Select(e => e.Id));
        Assert.Equal(first.Endpoints, second.Endpoints);
        Assert.Equal((1, EndpointIdentity.Version), (first.SchemaVersion, first.IdentityVersion));
    }

    [Fact]
    public void Snapshot_keeps_the_names_needed_for_display_and_rename_detection()
    {
        var entry = Assert.Single(AnalysisSnapshot.Create([Endpoint()]).Endpoints);

        Assert.Equal(("GET /api/products/{:int}", "GET", "api/Products/{id:int}", "Products", "GetById"),
            (entry.Id, entry.HttpMethod, entry.Route, entry.ControllerName, entry.ActionName));
        Assert.Equal(EndpointFingerprint.Compute(Endpoint()), entry.Fingerprint);
    }

    [Fact]
    public void Duplicate_identities_fail_loudly_instead_of_dropping_an_endpoint()
    {
        var exception = Assert.Throws<ArgumentException>(() => AnalysisSnapshot.Create(
        [
            Endpoint(route: "api/x/{id:int}", action: "ById"),
            Endpoint(route: "api/x/{count:int}", action: "ByCount")
        ]));

        Assert.Contains("GET /api/x/{:int}", exception.Message);
    }

    [Fact]
    public void Collection_reference_hashes_lf_normalized_text_and_keeps_only_the_file_name()
    {
        var lf = SnapshotCollection.FromJson(@"C:\some\folder\sample.postman_collection.json", "{\n  \"a\": 1\n}");
        var crlf = SnapshotCollection.FromJson("/other/place/sample.postman_collection.json", "{\r\n  \"a\": 1\r\n}");
        var different = SnapshotCollection.FromJson("sample.postman_collection.json", "{\n  \"a\": 2\n}");

        Assert.Equal("sample.postman_collection.json", lf.FileName);
        Assert.Equal(lf, crlf);
        Assert.NotEqual(lf.Sha256, different.Sha256);
        Assert.Matches("^[0-9a-f]{64}$", lf.Sha256);
    }
}
