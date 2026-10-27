using System.Text.Json;
using ApiCollection.Core.Models;
using ApiCollection.Postman.Generation;
using ApiCollection.Postman.Models;
using ApiCollection.Postman.Serialization;

namespace ApiCollection.Postman.Tests;

public sealed class PostmanCollectionGeneratorTests
{
    private static readonly PostmanCollectionGenerator Generator = new();

    private static EndpointDefinition Endpoint(
        string controller, string action, HttpMethodKind method, string route, Action<List<ParameterDefinition>>? parameters = null,
        RequestBodyDefinition? body = null, AuthenticationDefinition? auth = null, IReadOnlyList<ResponseDefinition>? responses = null,
        IReadOnlyList<ValidationRuleDefinition>? validation = null)
    {
        var list = new List<ParameterDefinition>();
        parameters?.Invoke(list);
        return new EndpointDefinition
        {
            Id = $"{method.ToString().ToUpperInvariant()} {route}",
            ControllerName = controller,
            ActionName = action,
            HttpMethod = method,
            RouteTemplate = route,
            Parameters = list,
            RequestBody = body,
            Authentication = auth ?? new AuthenticationDefinition(),
            Responses = responses ?? [],
            Validation = validation ?? []
        };
    }

    // ---- structure ----

    [Fact]
    public void Endpoints_are_grouped_into_controller_folders_with_http_method_and_url()
    {
        var collection = Generator.Generate(
        [
            Endpoint("Orders", "GetAll", HttpMethodKind.Get, "api/Orders"),
            Endpoint("Orders", "Delete", HttpMethodKind.Delete, "api/Orders/{id:int}", p => p.Add(
                new ParameterDefinition { Name = "id", Location = ParameterLocation.Path, TypeName = "int", IsRequired = true })),
            Endpoint("Products", "Create", HttpMethodKind.Post, "api/Products")
        ]);

        Assert.Equal(["Orders", "Products"], collection.Items.Select(f => f.Name));
        Assert.Equal(["GetAll", "Delete"], collection.Items[0].Items.Select(i => i.Name));
        Assert.Equal(["GET", "DELETE"], collection.Items[0].Items.Select(i => i.Request.Method));
        Assert.Equal("POST", collection.Items[1].Items[0].Request.Method);

        var delete = collection.Items[0].Items[1].Request.Url;
        Assert.Equal("{{baseUrl}}/api/Orders/:id", delete.Raw);
        Assert.Equal(["{{baseUrl}}"], delete.Host);
        Assert.Equal(["api", "Orders", ":id"], delete.Path);
        var variable = Assert.Single(delete.Variable!);
        Assert.Equal("id", variable.Key);
        Assert.Equal("", variable.Value);
        Assert.Equal("int, required", variable.Description);
        Assert.Null(collection.Items[0].Items[0].Request.Url.Variable);
    }

    [Fact]
    public void Route_placeholders_with_constraints_optional_marks_and_catch_all_become_path_variables()
    {
        var url = Generator.Generate([Endpoint("Files", "Get", HttpMethodKind.Get, "api/files/{id:guid}/{name?}/{*rest}")])
            .Items[0].Items[0].Request.Url;

        Assert.Equal(["api", "files", ":id", ":name", ":rest"], url.Path);
        Assert.Equal(["id", "name", "rest"], url.Variable!.Select(v => v.Key));
        Assert.Equal("{{baseUrl}}/api/files/:id/:name/:rest", url.Raw);
    }

    [Fact]
    public void Query_and_header_parameters_are_emitted_and_optional_ones_are_disabled()
    {
        var request = Generator.Generate([Endpoint("Items", "Search", HttpMethodKind.Get, "api/Items", p =>
        {
            p.Add(new ParameterDefinition { Name = "term", Location = ParameterLocation.Query, TypeName = "string", IsRequired = true });
            p.Add(new ParameterDefinition { Name = "page", Location = ParameterLocation.Query, TypeName = "int", IsRequired = false, DefaultValue = "1" });
            p.Add(new ParameterDefinition { Name = "X-Trace", Location = ParameterLocation.Header, TypeName = "string", IsRequired = false });
        })]).Items[0].Items[0].Request;

        Assert.Equal("{{baseUrl}}/api/Items?term=", request.Url.Raw);
        Assert.Collection(request.Url.Query!,
            q => Assert.Equal(("term", null), (q.Key, q.Disabled)),
            q => Assert.Equal(("page", (bool?)true), (q.Key, q.Disabled)));
        Assert.Equal("int, optional, default 1", request.Url.Query![1].Description);
        Assert.All(request.Url.Query, q => Assert.Equal("", q.Value));

        var header = Assert.Single(request.Headers);
        Assert.Equal(("X-Trace", (bool?)true), (header.Key, header.Disabled));
    }

    // ---- body ----

    [Fact]
    public void Body_uses_the_example_json_and_lists_its_sources()
    {
        var body = new RequestBodyDefinition
        {
            TypeName = "CreateItem",
            Example = new ExampleDefinition
            {
                Json = "{\r\n  \"name\": \"Laptop\"\r\n}",
                IsComplete = false,
                Fields = [new ExampleFieldSource { Path = "Name", Source = ExampleSource.XmlDocExample, SourceDetail = "Items.cs:4: CreateItem.Name" }],
                MissingFields = ["Price"]
            }
        };

        var request = Generator.Generate([Endpoint("Items", "Create", HttpMethodKind.Post, "api/Items", body: body)]).Items[0].Items[0].Request;

        Assert.Equal("raw", request.Body!.Mode);
        Assert.Equal("json", request.Body.Options.Raw.Language);
        Assert.Equal("{\n  \"name\": \"Laptop\"\n}", request.Body.Raw);
        Assert.Equal("Content-Type", request.Headers[0].Key);
        Assert.Equal("application/json", request.Headers[0].Value);
        Assert.Contains("Body example (incomplete)", request.Description);
        Assert.Contains("- `Name`: XmlDocExample (Items.cs:4: CreateItem.Name)", request.Description);
        Assert.Contains("Fields without an example (omitted): `Price`.", request.Description);
    }

    [Fact]
    public void Body_without_an_example_is_a_clearly_marked_skeleton_not_invented_data()
    {
        var body = new RequestBodyDefinition
        {
            TypeName = "Order",
            Properties =
            [
                new PropertyDefinition { Name = "Customer", TypeName = "string" },
                new PropertyDefinition { Name = "Total", TypeName = "decimal?" },
                new PropertyDefinition
                {
                    Name = "Lines", TypeName = "List<Line>",
                    Properties = [new PropertyDefinition { Name = "Qty", TypeName = "int" }]
                },
                new PropertyDefinition { Name = "Tags", TypeName = "string[]" },
                new PropertyDefinition { Name = "Sku", TypeName = "string", JsonName = "SKU" }
            ]
        };

        var request = Generator.Generate([Endpoint("Orders", "Create", HttpMethodKind.Post, "api/Orders", body: body)]).Items[0].Items[0].Request;

        using var json = JsonDocument.Parse(request.Body!.Raw);
        var root = json.RootElement;
        Assert.Equal("<string>", root.GetProperty("customer").GetString());
        Assert.Equal("<decimal>", root.GetProperty("total").GetString());
        Assert.Equal("<int>", root.GetProperty("lines")[0].GetProperty("qty").GetString());
        Assert.Equal("<string>", root.GetProperty("tags")[0].GetString());
        Assert.Equal("<string>", root.GetProperty("SKU").GetString());
        Assert.Contains("skeleton generated from the DTO contract", request.Description);
        Assert.Contains("placeholders, not real examples", request.Description);
    }

    [Fact]
    public void Endpoints_without_a_body_have_no_body_or_content_type()
    {
        var request = Generator.Generate([Endpoint("Items", "Get", HttpMethodKind.Get, "api/Items")]).Items[0].Items[0].Request;

        Assert.Null(request.Body);
        Assert.Empty(request.Headers);
    }

    // ---- authentication ----

    [Fact]
    public void Bearer_placeholder_is_added_only_to_protected_endpoints()
    {
        var collection = Generator.Generate(
        [
            Endpoint("Secure", "Open", HttpMethodKind.Get, "api/open"),
            Endpoint("Secure", "Anon", HttpMethodKind.Get, "api/anon", auth: new AuthenticationDefinition { RequiresAuthentication = false }),
            Endpoint("Secure", "Admin", HttpMethodKind.Get, "api/admin", auth: new AuthenticationDefinition
            {
                RequiresAuthentication = true, Scheme = "Bearer", Roles = ["Admin"], Policies = ["CanRead"]
            }),
            Endpoint("Secure", "Cookie", HttpMethodKind.Get, "api/cookie", auth: new AuthenticationDefinition { RequiresAuthentication = true, Scheme = "Cookies" })
        ]);

        var requests = collection.Items[0].Items.ToDictionary(i => i.Name, i => i.Request);
        Assert.Null(requests["Open"].Auth);
        Assert.Null(requests["Anon"].Auth);

        var auth = requests["Admin"].Auth!;
        Assert.Equal("bearer", auth.Type);
        var parameter = Assert.Single(auth.Bearer);
        Assert.Equal(("token", "{{token}}", "string"), (parameter.Key, parameter.Value, parameter.Type));
        Assert.Contains("Authentication: **required** (roles: Admin; policies: CanRead).", requests["Admin"].Description);
        Assert.Contains("{{token}}", requests["Admin"].Description);

        Assert.Null(requests["Cookie"].Auth);
        Assert.Contains("Scheme `Cookies` is not supported", requests["Cookie"].Description);
    }

    // ---- description, variables, naming ----

    [Fact]
    public void Description_lists_parameters_validation_and_response_examples_without_saving_fake_responses()
    {
        var endpoint = Endpoint(
            "Items", "Get", HttpMethodKind.Get, "api/Items/{id}",
            p => p.Add(new ParameterDefinition { Name = "id", Location = ParameterLocation.Path, TypeName = "int", IsRequired = true }),
            validation: [new ValidationRuleDefinition { PropertyName = "Name", RuleName = "MaximumLength", Arguments = ["100"] }],
            responses:
            [
                new ResponseDefinition
                {
                    StatusCode = 200,
                    Example = new ExampleDefinition
                    {
                        Json = "{ \"id\": 1 }",
                        IsComplete = true,
                        Fields = [new ExampleFieldSource { Path = "$", Source = ExampleSource.ExternalMetadata, SourceDetail = "examples.json" }]
                    }
                }
            ]);

        var item = Generator.Generate([endpoint]).Items[0].Items[0];

        Assert.Contains("**GET** `/api/Items/{id}`", item.Request.Description);
        Assert.Contains("Controller: `Items` · Action: `Get`", item.Request.Description);
        Assert.Contains("- `id` (path): int, required", item.Request.Description);
        Assert.Contains("- `Name`: MaximumLength(100)", item.Request.Description);
        Assert.Contains("Example response 200 (from ExternalMetadata; not a recorded response):", item.Request.Description);
        Assert.Empty(item.Response);
    }

    [Fact]
    public void Collection_has_info_schema_and_base_url_and_token_variables()
    {
        var collection = Generator.Generate([], new PostmanCollectionOptions { Name = "My API", BaseUrl = "https://localhost:7001" });

        Assert.Equal("My API", collection.Info.Name);
        Assert.Equal("https://schema.getpostman.com/json/collection/v2.1.0/collection.json", collection.Info.Schema);
        Assert.Empty(collection.Items);
        Assert.Equal([("baseUrl", "https://localhost:7001"), ("token", "")], collection.Variables.Select(v => (v.Key, v.Value)));
    }

    [Fact]
    public void Duplicate_action_names_in_a_folder_are_disambiguated_by_method_and_route()
    {
        var items = Generator.Generate(
        [
            Endpoint("Items", "Get", HttpMethodKind.Get, "api/Items"),
            Endpoint("Items", "Get", HttpMethodKind.Get, "api/Items/all"),
            Endpoint("Items", "Create", HttpMethodKind.Post, "api/Items")
        ]).Items[0].Items;

        Assert.Equal(["Get [GET /api/Items]", "Get [GET /api/Items/all]", "Create"], items.Select(i => i.Name));
    }

    // ---- serialization ----

    [Fact]
    public void Serialized_collection_is_valid_v21_json_with_stable_lf_output()
    {
        var endpoints = new[]
        {
            Endpoint("Items", "Create", HttpMethodKind.Post, "api/Items", body: new RequestBodyDefinition
            {
                TypeName = "CreateItem",
                Properties = [new PropertyDefinition { Name = "Name", TypeName = "string" }]
            }, auth: new AuthenticationDefinition { RequiresAuthentication = true })
        };

        var json = PostmanCollectionSerializer.Serialize(Generator.Generate(endpoints));
        var again = PostmanCollectionSerializer.Serialize(Generator.Generate(endpoints));

        Assert.Equal(json, again);
        Assert.DoesNotContain('\r', json);
        Assert.Contains("{{baseUrl}}", json);
        Assert.Contains("<string>", json);
        Assert.DoesNotContain("null", json);
        Assert.DoesNotContain("\\u", json);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(PostmanInfo.SchemaV21, root.GetProperty("info").GetProperty("schema").GetString());
        var request = root.GetProperty("item")[0].GetProperty("item")[0];
        Assert.Equal("Create", request.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Array, request.GetProperty("response").ValueKind);
        Assert.Equal("POST", request.GetProperty("request").GetProperty("method").GetString());
        Assert.Equal("bearer", request.GetProperty("request").GetProperty("auth").GetProperty("type").GetString());
    }
}
