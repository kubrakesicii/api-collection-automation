using ApiCollection.Analysis.Discovery;
using ApiCollection.Core.Models;

namespace ApiCollection.Analysis.Tests;

public sealed class EndpointDiscoveryEngineTests
{
    private static readonly EndpointDiscoveryEngine Engine = new();

    private static List<string> Describe(EndpointDiscoveryResult result) =>
        result.Endpoints
            .Select(e => $"{e.HttpMethod} {e.RouteTemplate} {e.ControllerName}.{e.ActionName}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

    private static List<string> Expected(params string[] endpoints) =>
        endpoints.OrderBy(s => s, StringComparer.Ordinal).ToList();

    [Fact]
    public void Controller_route_and_action_route_are_combined()
    {
        var result = Engine.DiscoverFromSources(["""
            using Microsoft.AspNetCore.Mvc;
            namespace App;
            [ApiController]
            [Route("api/[controller]")]
            public class ItemsController : ControllerBase
            {
                [HttpGet] public IActionResult List() => Ok();
                [HttpGet("{id:int}")] public IActionResult Get(int id) => Ok();
                [HttpPost] public IActionResult Create() => Ok();
                [HttpPut("{id:int}")] public IActionResult Update(int id) => Ok();
                [HttpDelete("{id:int}")] public IActionResult Delete(int id) => Ok();
                [HttpPatch("{id}")] public IActionResult Patch(int id) => Ok();
            }
            """]);

        Assert.Equal(Expected(
            "Get api/Items Items.List",
            "Get api/Items/{id:int} Items.Get",
            "Post api/Items Items.Create",
            "Put api/Items/{id:int} Items.Update",
            "Delete api/Items/{id:int} Items.Delete",
            "Patch api/Items/{id} Items.Patch"), Describe(result));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Controller_and_action_tokens_are_substituted()
    {
        var result = Engine.DiscoverFromSources(["""
            [Route("api/[controller]")]
            public class TokensController : ControllerBase
            {
                [HttpGet("[action]")] public IActionResult Ping() => Ok();
                [HttpGet("v1/[ACTION]")] public IActionResult Status() => Ok();
            }
            """]);

        Assert.Equal(Expected(
            "Get api/Tokens/Ping Tokens.Ping",
            "Get api/Tokens/v1/Status Tokens.Status"), Describe(result));
    }

    [Fact]
    public void Absolute_action_routes_ignore_the_controller_route()
    {
        var result = Engine.DiscoverFromSources(["""
            [Route("api/[controller]")]
            public class TokensController : ControllerBase
            {
                [HttpGet("/abs")] public IActionResult Abs() => Ok();
                [HttpGet("~/abs2")] public IActionResult Abs2() => Ok();
            }
            """]);

        Assert.Equal(Expected("Get abs Tokens.Abs", "Get abs2 Tokens.Abs2"), Describe(result));
    }

    [Fact]
    public void Multiple_http_attributes_and_route_attribute_on_action_are_supported()
    {
        var result = Engine.DiscoverFromSources(["""
            [Route("api/[controller]")]
            public class TokensController : ControllerBase
            {
                [HttpGet, HttpPost("multi")] public IActionResult Multi() => Ok();
                [Route("rt"), HttpDelete] public IActionResult Rt() => Ok();
            }
            """]);

        Assert.Equal(Expected(
            "Get api/Tokens Tokens.Multi",
            "Post api/Tokens/multi Tokens.Multi",
            "Delete api/Tokens/rt Tokens.Rt"), Describe(result));
    }

    [Fact]
    public void Base_controller_actions_and_route_are_inherited()
    {
        var result = Engine.DiscoverFromSources(["""
            [Route("api/base")]
            public abstract class BaseController : ControllerBase
            {
                [HttpGet] public IActionResult List() => Ok();
            }
            public class PlainController : BaseController { }
            """]);

        Assert.Equal(Expected("Get api/base Plain.List"), Describe(result));
    }

    [Fact]
    public void Derived_route_overrides_base_route_and_inherits_base_actions()
    {
        var result = Engine.DiscoverFromSources(["""
            [Route("api/base")]
            public abstract class BaseController : ControllerBase
            {
                [HttpGet] public IActionResult List() => Ok();
            }
            [Route("api/derived")]
            public class DerivedController : BaseController
            {
                [HttpGet("{id}")] public IActionResult One(int id) => Ok();
            }
            """]);

        Assert.Equal(Expected("Get api/derived Derived.List", "Get api/derived/{id} Derived.One"), Describe(result));
    }

    [Fact]
    public void Controller_token_uses_the_concrete_controller_name_for_inherited_actions()
    {
        var result = Engine.DiscoverFromSources(["""
            [Route("api/[controller]")]
            public abstract class CrudController : ControllerBase
            {
                [HttpGet] public IActionResult List() => Ok();
            }
            public class OrdersController : CrudController { }
            public class UsersController : CrudController { }
            """]);

        Assert.Equal(Expected("Get api/Orders Orders.List", "Get api/Users Users.List"), Describe(result));
    }

    [Fact]
    public void Base_class_declared_in_a_later_source_is_resolved()
    {
        var result = Engine.DiscoverFromSources([
            "public class ChildController : BaseController { }",
            "[Route(\"api/base\")] public abstract class BaseController : ControllerBase { [HttpGet(\"x\")] public IActionResult X() => Ok(); }"
        ]);

        Assert.Equal(Expected("Get api/base/x Child.X"), Describe(result));
    }

    [Fact]
    public void Non_controllers_and_non_actions_are_ignored()
    {
        var result = Engine.DiscoverFromSources(["""
            [Route("api/helper")]
            public class Helper { [HttpGet] public IActionResult Nope() => null!; }

            [Route("api/[controller]")]
            public abstract class AbstractController : ControllerBase { [HttpGet] public IActionResult A() => Ok(); }

            [Route("api/[controller]")]
            internal class HiddenController : ControllerBase { [HttpGet] public IActionResult A() => Ok(); }

            [Route("api/[controller]")]
            public class RealController : ControllerBase
            {
                [HttpGet] public IActionResult Visible() => Ok();
                [NonAction, HttpGet("skip")] public IActionResult Skipped() => Ok();
                [HttpGet("private")] private IActionResult Private() => Ok();
                [HttpGet("static")] public static IActionResult Static() => null!;
                public IActionResult NoHttpAttribute() => Ok();
            }
            """]);

        Assert.Equal(Expected("Get api/Real Real.Visible"), Describe(result));
    }

    [Fact]
    public void Namespaces_partial_classes_and_qualified_attributes_are_handled()
    {
        var result = Engine.DiscoverFromSources([
            """
            namespace App.Web;
            [Microsoft.AspNetCore.Mvc.Route("api/[controller]")]
            public partial class ThingsController : Microsoft.AspNetCore.Mvc.ControllerBase
            {
                [Microsoft.AspNetCore.Mvc.HttpGetAttribute] public object List() => 1;
            }
            """,
            """
            namespace App.Web
            {
                public partial class ThingsController
                {
                    [HttpPost] public object Create() => 1;
                }
            }
            """]);

        Assert.Equal(Expected("Get api/Things Things.List", "Post api/Things Things.Create"), Describe(result));
    }

    [Fact]
    public void Literal_concatenation_is_resolved_and_other_expressions_are_reported()
    {
        var result = Engine.DiscoverFromSources(["""
            [Route("api/" + "[controller]")]
            public class ThingsController : ControllerBase
            {
                [HttpGet("ok")] public IActionResult Ok1() => Ok();
                [HttpGet(Routes.Bad)] public IActionResult Bad() => Ok();
            }
            [Route(Routes.Base)]
            public class OtherController : ControllerBase
            {
                [HttpGet] public IActionResult Lost() => Ok();
            }
            """]);

        Assert.Equal(Expected("Get api/Things/ok Things.Ok1"), Describe(result));
        Assert.Contains("ThingsController.Bad: route template is not a string literal and was skipped.", result.Warnings);
        Assert.Contains("OtherController: route template is not a string literal and was skipped.", result.Warnings);
    }

    [Fact]
    public void Route_without_http_attribute_and_unknown_tokens_are_reported()
    {
        var result = Engine.DiscoverFromSources(["""
            [Route("api/[controller]")]
            public class ThingsController : ControllerBase
            {
                [Route("any")] public IActionResult Any() => Ok();
                [HttpGet("[area]/x")] public IActionResult Area() => Ok();
                [HttpGet("{id:regex([a-z]+)}")] public IActionResult Regex(string id) => Ok();
            }
            """]);

        Assert.Contains("ThingsController.Any: [Route] without an HTTP method attribute is not supported and was skipped.", result.Warnings);
        Assert.Contains(result.Warnings, w => w.StartsWith("ThingsController.Area:") && w.Contains("unsupported token"));
        Assert.DoesNotContain(result.Warnings, w => w.StartsWith("ThingsController.Regex"));
    }

    [Fact]
    public void Output_is_deterministic_and_independent_of_source_order()
    {
        string[] sources =
        [
            "[Route(\"api/[controller]\")] public class BController : ControllerBase { [HttpPost] public object P() => 1; [HttpGet] public object G() => 1; }",
            "[Route(\"api/[controller]\")] public class AController : ControllerBase { [HttpGet(\"z\")] public object Z() => 1; [HttpGet(\"a\")] public object A() => 1; }"
        ];

        var first = Engine.DiscoverFromSources(sources).Endpoints.Select(e => e.Id + "|" + e.ActionName).ToList();
        var second = Engine.DiscoverFromSources(sources.Reverse()).Endpoints.Select(e => e.Id + "|" + e.ActionName).ToList();

        Assert.Equal(first, second);
        Assert.Equal(["GET api/A/a|A", "GET api/A/z|Z", "GET api/B|G", "POST api/B|P"], first);
    }

    [Fact]
    public void Directory_discovery_skips_build_output_and_does_not_modify_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "ApiCollection.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Controllers"));
        Directory.CreateDirectory(Path.Combine(root, "obj", "Debug"));
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        const string controller = "[Route(\"api/[controller]\")] public class LiveController : ControllerBase { [HttpGet] public object G() => 1; }";
        var live = Path.Combine(root, "Controllers", "LiveController.cs");
        File.WriteAllText(live, controller);
        File.WriteAllText(Path.Combine(root, "obj", "Debug", "Generated.cs"), controller.Replace("Live", "Gen"));
        File.WriteAllText(Path.Combine(root, "bin", "Copy.cs"), controller.Replace("Live", "Bin"));
        var before = File.GetLastWriteTimeUtc(live);

        try
        {
            var result = Engine.DiscoverFromDirectory(root);

            Assert.Equal(Expected("Get api/Live Live.G"), Describe(result));
            Assert.Equal(controller, File.ReadAllText(live));
            Assert.Equal(before, File.GetLastWriteTimeUtc(live));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
