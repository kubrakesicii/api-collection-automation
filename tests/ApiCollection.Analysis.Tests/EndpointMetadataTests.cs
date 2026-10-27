using ApiCollection.Analysis.Discovery;
using ApiCollection.Core.Models;

namespace ApiCollection.Analysis.Tests;

public sealed class EndpointMetadataTests
{
    private static readonly EndpointDiscoveryEngine Engine = new();

    private static EndpointDiscoveryResult Discover(params string[] sources) => Engine.DiscoverFromSources(sources);

    private static EndpointDefinition Single(EndpointDiscoveryResult result, string action) =>
        Assert.Single(result.Endpoints, e => e.ActionName == action);

    private static string Describe(ParameterDefinition p) =>
        $"{p.Location} {p.Name}:{p.TypeName}{(p.IsRequired ? " required" : "")}{(p.DefaultValue is null ? "" : " =" + p.DefaultValue)}";

    // ---- parameters ----

    private const string ItemsController = """
        [ApiController]
        [Route("api/[controller]")]
        public class ItemsController : ControllerBase
        {
            [HttpGet("{id:int}")]
            public IActionResult Get(int id, string? filter, int page = 1, string sort = "name", CancellationToken ct = default) => Ok();

            [HttpPost]
            public IActionResult Create(CreateItem request) => Ok();

            [HttpPut("{id}")]
            public IActionResult Update(int id, [FromBody] UpdateItem body, [FromHeader(Name = "X-Trace")] string trace, [FromServices] IClock clock) => Ok();

            [HttpGet("search")]
            public IActionResult Search([FromQuery] SearchModel model) => Ok();

            [HttpGet("by/{key}")]
            public IActionResult ByKey([FromRoute(Name = "key")] Guid id, [FromQuery(Name = "q")] string text) => Ok();
        }

        public record CreateItem(string Name, decimal Price);
        public record UpdateItem(string Name);
        public class SearchModel { public string? Term { get; set; } public required int Page { get; set; } }
        """;

    [Fact]
    public void Path_query_header_and_body_parameters_are_classified()
    {
        var result = Discover(ItemsController);

        Assert.Equal(
            ["Path id:int required", "Query filter:string?", "Query page:int =1", "Query sort:string =\"name\""],
            Single(result, "Get").Parameters.Select(Describe));
        Assert.Equal(["Body request:CreateItem required"], Single(result, "Create").Parameters.Select(Describe));
        Assert.Equal(
            ["Path id:int required", "Body body:UpdateItem required", "Header X-Trace:string required"],
            Single(result, "Update").Parameters.Select(Describe));
    }

    [Fact]
    public void Explicit_binding_names_and_complex_query_models_are_resolved()
    {
        var result = Discover(ItemsController);

        Assert.Equal(["Query Term:string?", "Query Page:int required"], Single(result, "Search").Parameters.Select(Describe));
        Assert.Equal(["Path key:Guid required", "Query q:string required"], Single(result, "ByKey").Parameters.Select(Describe));
    }

    [Fact]
    public void Request_body_lists_properties_with_nullability_and_required_markers()
    {
        var result = Discover("""
            [ApiController]
            [Route("api/[controller]")]
            public class OrdersController : ControllerBase
            {
                [HttpPost] public IActionResult Create(CreateOrder order) => Ok();
            }
            public class CreateOrder : OrderBase
            {
                [Required] public string Customer { get; set; } = "";
                public string? Note { get; set; }
                public required int Quantity { get; init; }
                public int? Priority { get; set; }
                [JsonIgnore] public string Hidden { get; set; } = "";
                private string Secret { get; set; } = "";
                public static int Counter { get; set; }
            }
            public abstract class OrderBase { public Guid Id { get; set; } }
            """);

        var body = Single(result, "Create").RequestBody!;
        Assert.Equal("CreateOrder", body.TypeName);
        Assert.Equal("application/json", body.ContentType);
        Assert.True(body.IsRequired);
        Assert.Equal(
            ["Id:Guid", "Customer:string (required)", "Note:string? (nullable)", "Quantity:int (required)", "Priority:int? (nullable)"],
            body.Properties.Select(p => $"{p.Name}:{p.TypeName}{(p.IsRequired ? " (required)" : "")}{(p.IsNullable ? " (nullable)" : "")}"));
    }

    [Fact]
    public void Nested_dtos_collections_and_records_are_expanded()
    {
        var result = Discover("""
            [ApiController]
            [Route("api/[controller]")]
            public class PeopleController : ControllerBase
            {
                [HttpPost] public IActionResult Create(Person person) => Ok();
            }
            public record Person(string Name, Address? Home, List<Address> Others, Tag[] Tags, Status Status);
            public record Address([property: Required] string City, GeoPoint Location);
            public record GeoPoint(double Lat, double Lng);
            public record Tag(string Label);
            public enum Status { Active, Inactive }
            """);

        var person = Single(result, "Create").RequestBody!.Properties;
        Assert.Equal(["Name", "Home", "Others", "Tags", "Status"], person.Select(p => p.Name));

        var home = person.Single(p => p.Name == "Home");
        Assert.True(home.IsNullable);
        Assert.Equal(["City", "Location"], home.Properties.Select(p => p.Name));
        Assert.True(home.Properties[0].IsRequired);
        Assert.Equal(["Lat", "Lng"], home.Properties[1].Properties.Select(p => p.Name));

        Assert.Equal(["City", "Location"], person.Single(p => p.Name == "Others").Properties.Select(p => p.Name));
        Assert.Equal(["Label"], person.Single(p => p.Name == "Tags").Properties.Select(p => p.Name));
        Assert.Empty(person.Single(p => p.Name == "Status").Properties);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Recursive_unknown_and_generic_types_are_reported_not_guessed()
    {
        var result = Discover("""
            [ApiController]
            [Route("api/[controller]")]
            public class NodesController : ControllerBase
            {
                [HttpPost] public IActionResult Create(Node node) => Ok();
                [HttpPut] public IActionResult Update(Wrapper wrapper) => Ok();
            }
            public class Node { public string Name { get; set; } = ""; public Node? Parent { get; set; } }
            public class Wrapper { public ThirdPartyThing Thing { get; set; } = null!; public Page<string> Page { get; set; } = null!; }
            public class Page<T> { public T Value { get; set; } = default!; }
            """);

        Assert.Equal(["Name", "Parent"], Single(result, "Create").RequestBody!.Properties.Select(p => p.Name));
        Assert.Empty(Single(result, "Create").RequestBody!.Properties[1].Properties);
        Assert.Contains(result.Warnings, w => w.Contains("'Node' is recursive"));
        Assert.Contains(result.Warnings, w => w.Contains("type 'ThirdPartyThing' was not found"));
        Assert.Contains(result.Warnings, w => w.Contains("generic type 'Page'"));
    }

    [Fact]
    public void Complex_parameters_without_api_controller_and_unknown_types_are_reported()
    {
        var result = Discover("""
            [Route("api/[controller]")]
            public class LegacyController : ControllerBase
            {
                [HttpPost] public IActionResult Create(Dto dto) => Ok();
                [HttpGet] public IActionResult Find(Mystery thing, [FromForm] Dto form) => Ok();
            }
            public class Dto { public string Name { get; set; } = ""; }
            """);

        Assert.Empty(Single(result, "Create").Parameters);
        Assert.Null(Single(result, "Create").RequestBody);
        Assert.Contains(result.Warnings, w => w.Contains("cannot be inferred without [ApiController]"));
        Assert.Contains(result.Warnings, w => w.Contains("'thing' (Mystery) could not be determined"));
        Assert.Contains(result.Warnings, w => w.Contains("[FromForm]"));
    }

    // ---- authentication ----

    [Fact]
    public void Authorize_and_allow_anonymous_are_resolved_across_class_and_action()
    {
        var result = Discover("""
            [Route("api/base")]
            [Authorize(Roles = "Admin, Editor", AuthenticationSchemes = "Bearer")]
            public abstract class SecureBase : ControllerBase { }

            [Route("api/secure")]
            public class SecureController : SecureBase
            {
                [HttpGet("plain")] public IActionResult Plain() => Ok();
                [HttpGet("policy")] [Authorize("CanRead")] public IActionResult Policy() => Ok();
                [HttpGet("open")] [AllowAnonymous] public IActionResult Open() => Ok();
            }

            [Route("api/public")]
            public class PublicController : ControllerBase
            {
                [HttpGet] public IActionResult Index() => Ok();
                [HttpGet("me")] [Authorize] public IActionResult Me() => Ok();
            }
            """);

        var plain = Single(result, "Plain").Authentication;
        Assert.True(plain.RequiresAuthentication);
        Assert.Equal("Bearer", plain.Scheme);
        Assert.Equal(["Admin", "Editor"], plain.Roles);
        Assert.Empty(plain.Policies);

        var policy = Single(result, "Policy").Authentication;
        Assert.Equal(["CanRead"], policy.Policies);
        Assert.Equal(["Admin", "Editor"], policy.Roles);

        Assert.False(Single(result, "Open").Authentication.RequiresAuthentication);
        Assert.False(Single(result, "Index").Authentication.RequiresAuthentication);

        var me = Single(result, "Me").Authentication;
        Assert.True(me.RequiresAuthentication);
        Assert.Null(me.Scheme);
    }

    [Fact]
    public void Non_literal_authorize_arguments_and_global_authorization_are_reported()
    {
        var result = Discover("""
            [Route("api/[controller]")]
            [Authorize(Roles = Roles.Admin)]
            public class AdminController : ControllerBase
            {
                [HttpGet] public IActionResult Index() => Ok();
            }
            app.MapControllers().RequireAuthorization();
            """);

        var auth = Single(result, "Index").Authentication;
        Assert.True(auth.RequiresAuthentication);
        Assert.Empty(auth.Roles);
        Assert.Contains(result.Warnings, w => w.Contains("[Authorize] argument 'Roles' is not a string literal"));
        Assert.Contains(result.Warnings, w => w.StartsWith("Global authorization"));
    }

    // ---- FluentValidation ----

    private const string CreateController = """
        [ApiController]
        [Route("api/[controller]")]
        public class ThingsController : ControllerBase
        {
            [HttpPost] public IActionResult Create(CreateThing request) => Ok();
        }
        public record CreateThing(string Name, int Age, string Email, Address Address, List<Line> Lines);
        public record Address(string City);
        public record Line(int Qty);
        """;

    private static string Describe(ValidationRuleDefinition r) => $"{r.PropertyName} {r.RuleName}({string.Join(",", r.Arguments)})";

    [Fact]
    public void Static_fluent_validation_rules_are_extracted()
    {
        var result = Discover(CreateController, """
            public class CreateThingValidator : AbstractValidator<CreateThing>
            {
                public CreateThingValidator()
                {
                    RuleFor(x => x.Name).NotEmpty().WithMessage("required").MaximumLength(100);
                    RuleFor(x => x.Age).InclusiveBetween(0, 150);
                    RuleFor(x => x.Email).EmailAddress().Matches(@"^\S+$");
                    RuleFor(x => x.Address).SetValidator(new AddressValidator());
                    RuleForEach(x => x.Lines).SetValidator(new LineValidator());
                }
            }
            public class AddressValidator : AbstractValidator<Address>
            {
                public AddressValidator() { RuleFor(a => a.City).NotNull().Length(2, 50); }
            }
            public class LineValidator : AbstractValidator<Line>
            {
                public LineValidator() { RuleFor(l => l.Qty).GreaterThan(0); }
            }
            """);

        Assert.Equal(
            [
                "Name NotEmpty()", "Name MaximumLength(100)", "Age InclusiveBetween(0,150)",
                "Email EmailAddress()", @"Email Matches(^\S+$)",
                "Address.City NotNull()", "Address.City Length(2,50)", "Lines[].Qty GreaterThan(0)"
            ],
            Single(result, "Create").Validation.Select(Describe));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Dynamic_conditional_and_unresolvable_rules_are_reported_not_invented()
    {
        var result = Discover(CreateController, """
            public class CreateThingValidator : AbstractValidator<CreateThing>
            {
                private const int Max = 10;
                public CreateThingValidator()
                {
                    RuleFor(x => x.Name).NotEmpty();
                    RuleFor(x => x.Name).Must(n => n != "x");
                    RuleFor(x => x.Age).GreaterThan(Max);
                    RuleFor(x => x.Email).EmailAddress().When(x => x.Age > 18);
                    RuleFor(x => x.Name.Trim()).NotEmpty();
                    RuleFor(x => x.Address).SetValidator(new MissingValidator());
                    RuleSet("extra", () => { RuleFor(x => x.Age).NotNull(); });
                    if (Max > 5) { RuleFor(x => x.Name).MinimumLength(2); }
                }
            }
            """);

        Assert.Equal(["Name NotEmpty()"], Single(result, "Create").Validation.Select(Describe));
        Assert.Contains(result.Warnings, w => w.Contains("rule 'Must' on 'Name' cannot be resolved statically"));
        Assert.Contains(result.Warnings, w => w.Contains("rule 'GreaterThan' on 'Age' has non-literal arguments"));
        Assert.Contains(result.Warnings, w => w.Contains("conditional rule (When) on 'Email'"));
        Assert.Contains(result.Warnings, w => w.Contains("property expression in 'RuleFor(x => x.Name.Trim())'"));
        Assert.Contains(result.Warnings, w => w.Contains("SetValidator on 'Address'"));
        Assert.Contains(result.Warnings, w => w.Contains("unsupported statement 'RuleSet("));
        Assert.Contains(result.Warnings, w => w.Contains("unsupported statement 'if (Max > 5)"));
    }

    [Fact]
    public void Validators_only_apply_to_their_request_body_type()
    {
        var result = Discover(CreateController, """
            public class OtherValidator : AbstractValidator<Address>
            {
                public OtherValidator() { RuleFor(a => a.City).NotEmpty(); }
            }
            """);

        Assert.Empty(Single(result, "Create").Validation);
    }
}
