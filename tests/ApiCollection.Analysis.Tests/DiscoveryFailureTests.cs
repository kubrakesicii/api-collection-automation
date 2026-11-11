using ApiCollection.Analysis.Discovery;

namespace ApiCollection.Analysis.Tests;

/// <summary>
/// A discovery <b>failure</b> may hide endpoints (so a sync must not trust the result); a <b>warning</b> only makes details less complete.
/// Failures also appear in Warnings, so existing consumers keep seeing them.
/// </summary>
public sealed class DiscoveryFailureTests
{
    private static readonly EndpointDiscoveryEngine Engine = new();

    private const string Good = """
        [ApiController]
        [Route("api/[controller]")]
        public class ItemsController : ControllerBase
        {
            [HttpGet] public IActionResult List() => Ok();
        }
        """;

    [Fact]
    public void A_clean_source_has_neither_warnings_nor_failures()
    {
        var result = Engine.DiscoverFromSources([Good]);

        Assert.Empty(result.Failures);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void The_sample_api_has_no_failures()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ApiCollection.sln")))
        {
            directory = directory.Parent;
        }

        var result = Engine.DiscoverFromDirectory(Path.Combine(directory!.FullName, "fixtures", "SampleApi"));

        Assert.Equal(15, result.Endpoints.Count);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void A_source_that_does_not_parse_is_a_failure_because_the_parser_may_have_dropped_endpoints()
    {
        var broken = Good.Replace("public IActionResult List() => Ok();", "public IActionResult List() => Ok(;");

        var result = Engine.DiscoverFromSources([Good.Replace("Items", "Other"), broken]);

        var failure = Assert.Single(result.Failures);
        Assert.StartsWith("source #2: the source has", failure);
        Assert.Contains("syntax error(s)", failure);
        Assert.Contains("endpoints in it may be missing", failure);
        Assert.Contains(failure, result.Warnings);
    }

    [Fact]
    public void The_failure_names_the_file_when_the_source_comes_from_a_directory()
    {
        var work = Path.Combine(Path.GetTempPath(), "ApiCollection.Failures." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(work, "Controllers"));
        try
        {
            File.WriteAllText(Path.Combine(work, "Controllers", "ItemsController.cs"), Good + "\n}");

            var result = Engine.DiscoverFromDirectory(work);

            Assert.StartsWith("Controllers/ItemsController.cs: the source has", Assert.Single(result.Failures));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Theory]
    [InlineData("[Route(Routes.Base)] public class AController : ControllerBase { [HttpGet] public object G() => 1; }", "AController: route template is not a string literal")]
    [InlineData("[Route(\"api\")] public class AController : ControllerBase { [HttpGet(Routes.One)] public object G() => 1; }", "AController.G: route template is not a string literal")]
    [InlineData("[Route(\"api\")] public class AController : ControllerBase { [Route(\"x\")] public object G() => 1; }", "AController.G: [Route] without an HTTP method attribute")]
    [InlineData("[Route(\"api\")] public class AController : ControllerBase { [HttpGet(\"[area]/x\")] public object G() => 1; }", "AController.G: route 'api/[area]/x' contains an unsupported token")]
    public void Routes_that_cannot_be_resolved_are_failures(string source, string expected)
    {
        var result = Engine.DiscoverFromSources([source]);

        Assert.Contains(result.Failures, f => f.Contains(expected));
        Assert.All(result.Failures, f => Assert.Contains(f, result.Warnings));
    }

    [Fact]
    public void Two_actions_with_the_same_identity_are_a_failure()
    {
        var result = Engine.DiscoverFromSources(["""
            [Route("api/x")]
            public class XController : ControllerBase
            {
                [HttpGet("{id:int}")] public object ById(int id) => 1;
                [HttpGet("{count:int}")] public object ByCount(int count) => 1;
            }
            """]);

        Assert.Contains(result.Failures, f => f.Contains("is produced by more than one action"));
    }

    [Theory]
    [InlineData("public class Dto { public Unknown Thing { get; set; } = null!; }", "type 'Unknown' was not found")]
    [InlineData("public class Dto { public Page<string> Page { get; set; } = null!; } public class Page<T> { public T V { get; set; } = default!; }", "generic type 'Page'")]
    public void Details_that_cannot_be_resolved_are_warnings_but_not_failures(string dto, string expected)
    {
        var result = Engine.DiscoverFromSources(["""
            [ApiController]
            [Route("api/[controller]")]
            public class ItemsController : ControllerBase
            {
                [HttpPost] public IActionResult Create(Dto request) => Ok();
            }
            """, dto]);

        Assert.Contains(result.Warnings, w => w.Contains(expected));
        Assert.Empty(result.Failures);
        Assert.Single(result.Endpoints);
    }

    [Fact]
    public void Unsupported_binding_and_global_authorization_notes_are_warnings_but_not_failures()
    {
        var result = Engine.DiscoverFromSources(["""
            [ApiController]
            [Route("api/[controller]")]
            public class ItemsController : ControllerBase
            {
                [HttpPost] public IActionResult Upload([FromForm] string name, Mystery thing) => Ok();
            }
            """, "app.MapControllers().RequireAuthorization();"]);

        Assert.Contains(result.Warnings, w => w.Contains("[FromForm]"));
        Assert.Contains(result.Warnings, w => w.StartsWith("Global authorization"));
        Assert.Empty(result.Failures);
    }
}
