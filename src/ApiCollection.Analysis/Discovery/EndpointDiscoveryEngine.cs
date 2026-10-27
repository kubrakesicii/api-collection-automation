using System.Text.RegularExpressions;
using ApiCollection.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static ApiCollection.Analysis.Discovery.SyntaxHelpers;

namespace ApiCollection.Analysis.Discovery;

/// <summary>
/// Discovers attribute-routed MVC controller endpoints from C# source using Roslyn syntax trees.
/// Source is only parsed; the analyzed project is never built, loaded or executed.
/// </summary>
public sealed partial class EndpointDiscoveryEngine
{
    private static readonly Dictionary<string, HttpMethodKind> HttpAttributes = new()
    {
        ["HttpGet"] = HttpMethodKind.Get,
        ["HttpPost"] = HttpMethodKind.Post,
        ["HttpPut"] = HttpMethodKind.Put,
        ["HttpPatch"] = HttpMethodKind.Patch,
        ["HttpDelete"] = HttpMethodKind.Delete,
        ["HttpHead"] = HttpMethodKind.Head,
        ["HttpOptions"] = HttpMethodKind.Options
    };

    public EndpointDiscoveryResult DiscoverFromDirectory(string directory)
    {
        var files = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(Path.GetRelativePath(directory, path)))
            .OrderBy(path => path, StringComparer.Ordinal);

        return DiscoverFromSources(files.Select(File.ReadAllText));
    }

    public EndpointDiscoveryResult DiscoverFromSources(IEnumerable<string> sources)
    {
        var texts = sources.ToList();
        var warnings = new List<string>();
        var roots = texts.Select(text => CSharpSyntaxTree.ParseText(text).GetRoot()).ToList();
        var classes = CollectClasses(roots);
        var extractor = new EndpointMetadataExtractor(new SourceCatalog(roots));
        var endpoints = new List<EndpointDefinition>();

        if (texts.Any(t => t.Contains("RequireAuthorization(", StringComparison.Ordinal) || t.Contains("FallbackPolicy", StringComparison.Ordinal)))
        {
            warnings.Add("Global authorization (RequireAuthorization/FallbackPolicy) is not analyzed; authentication reflects [Authorize]/[AllowAnonymous] attributes only.");
        }

        foreach (var group in classes.Where(IsConcretePublicClass))
        {
            var chain = BuildChain(group, classes, out var reachesFramework);
            if (IsController(group, chain, reachesFramework))
            {
                endpoints.AddRange(DiscoverController(group, chain, extractor, warnings));
            }
        }

        var ordered = endpoints
            .DistinctBy(e => (e.ControllerName, e.ActionName, e.Id))
            .OrderBy(e => e.ControllerName, StringComparer.Ordinal)
            .ThenBy(e => e.RouteTemplate, StringComparer.Ordinal)
            .ThenBy(e => e.HttpMethod)
            .ThenBy(e => e.ActionName, StringComparer.Ordinal)
            .ToList();

        return new EndpointDiscoveryResult(ordered, warnings.Distinct().ToList());
    }

    private static bool IsBuildOutput(string relativePath) =>
        relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
                         || segment.Equals("obj", StringComparison.OrdinalIgnoreCase));

    // ---- class collection and inheritance ----

    private sealed record ClassGroup(string Name, string Namespace, List<ClassDeclarationSyntax> Declarations);

    private static List<ClassGroup> CollectClasses(IEnumerable<SyntaxNode> roots)
    {
        var groups = new List<ClassGroup>();
        var byFullName = new Dictionary<string, ClassGroup>();

        foreach (var root in roots)
        {
            foreach (var declaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var ns = string.Join('.', declaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
                    .Reverse().Select(n => n.Name.ToString()));
                var outer = declaration.Ancestors().OfType<TypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.Text);
                var fullName = string.Join('.', new[] { ns }.Concat(outer).Append(declaration.Identifier.Text)
                    .Where(part => part.Length > 0));

                if (!byFullName.TryGetValue(fullName, out var group))
                {
                    group = new ClassGroup(declaration.Identifier.Text, ns, []);
                    byFullName[fullName] = group;
                    groups.Add(group);
                }

                group.Declarations.Add(declaration);
            }
        }

        return groups;
    }

    private static bool IsConcretePublicClass(ClassGroup group) =>
        group.Declarations.Any(d => d.Modifiers.Any(SyntaxKind.PublicKeyword))
        && !group.Declarations.Any(d => d.Modifiers.Any(SyntaxKind.AbstractKeyword))
        && !group.Declarations.Any(d => d.TypeParameterList is not null);

    /// <summary>Returns the class followed by its resolvable base classes, nearest first.</summary>
    private static List<ClassGroup> BuildChain(ClassGroup group, List<ClassGroup> classes, out bool reachesFramework)
    {
        var chain = new List<ClassGroup>();
        reachesFramework = false;
        ClassGroup? current = group;

        while (current is not null && !chain.Contains(current))
        {
            chain.Add(current);
            ClassGroup? next = null;

            foreach (var baseName in current.Declarations.SelectMany(BaseTypeNames))
            {
                if (baseName is "ControllerBase" or "Controller")
                {
                    reachesFramework = true;
                    continue;
                }

                next = ResolveClass(classes, current.Namespace, baseName);
                if (next is not null)
                {
                    break;
                }
            }

            current = next;
        }

        return chain;
    }

    private static ClassGroup? ResolveClass(List<ClassGroup> classes, string currentNamespace, string name)
    {
        var candidates = classes.Where(c => c.Name == name).ToList();
        return candidates.FirstOrDefault(c => c.Namespace == currentNamespace) ?? candidates.FirstOrDefault();
    }

    private static IEnumerable<string> BaseTypeNames(ClassDeclarationSyntax declaration) =>
        declaration.BaseList?.Types.Select(t => LastIdentifier(t.Type)) ?? [];

    private static bool IsController(ClassGroup group, List<ClassGroup> chain, bool reachesFramework)
    {
        if (HasAttribute(group, "NonController"))
        {
            return false;
        }

        return reachesFramework
            || group.Name.EndsWith("Controller", StringComparison.Ordinal)
            || chain.Any(g => HasAttribute(g, "Controller"));
    }

    // ---- endpoint discovery ----

    private static IEnumerable<EndpointDefinition> DiscoverController(
        ClassGroup group, List<ClassGroup> chain, EndpointMetadataExtractor extractor, List<string> warnings)
    {
        var classAttributes = chain.SelectMany(g => g.Declarations).SelectMany(Attributes).ToList();
        var controllerName = group.Name.Length > "Controller".Length && group.Name.EndsWith("Controller", StringComparison.Ordinal)
            ? group.Name[..^"Controller".Length]
            : group.Name;

        // Route attributes are inherited, but a class's own [Route] overrides the base class's (verified against ASP.NET Core 8):
        // the nearest class in the chain that declares any [Route] supplies the controller templates.
        var classRoutes = chain
            .Select(g => g.Declarations.SelectMany(Attributes).Where(a => AttributeName(a) == "Route").ToList())
            .FirstOrDefault(routes => routes.Count > 0) ?? [];
        List<string> controllerTemplates;
        if (classRoutes.Count == 0)
        {
            controllerTemplates = [string.Empty];
        }
        else
        {
            controllerTemplates = ResolveTemplates(classRoutes, group.Name, warnings);
            if (controllerTemplates.Count == 0)
            {
                yield break;
            }
        }

        var seen = new HashSet<string>();
        foreach (var method in chain.SelectMany(g => g.Declarations).SelectMany(d => d.Members).OfType<MethodDeclarationSyntax>())
        {
            if (!IsAction(method))
            {
                continue;
            }

            var attributes = Attributes(method).ToList();
            var signature = method.Identifier.Text + "(" + string.Join(",", method.ParameterList.Parameters.Select(p => p.Type?.ToString())) + ")";
            var isBareOverride = method.Modifiers.Any(SyntaxKind.OverrideKeyword) && attributes.Count == 0;
            if (seen.Contains(signature))
            {
                continue;
            }

            if (!isBareOverride)
            {
                seen.Add(signature);
            }

            var httpAttributes = attributes.Where(a => HttpAttributes.ContainsKey(AttributeName(a))).ToList();
            var routeAttributes = attributes.Where(a => AttributeName(a) == "Route").ToList();
            var owner = $"{group.Name}.{method.Identifier.Text}";

            if (httpAttributes.Count == 0)
            {
                if (routeAttributes.Count > 0)
                {
                    warnings.Add($"{owner}: [Route] without an HTTP method attribute is not supported and was skipped.");
                }

                continue;
            }

            var routeTemplates = ResolveTemplates(routeAttributes, owner, warnings);
            foreach (var http in httpAttributes)
            {
                List<string> actionTemplates;
                var templateArgument = TemplateArgument(http);
                if (templateArgument is not null)
                {
                    if (!TryEvaluate(templateArgument, out var template))
                    {
                        warnings.Add($"{owner}: route template is not a string literal and was skipped.");
                        continue;
                    }

                    actionTemplates = [template];
                }
                else if (routeAttributes.Count > 0)
                {
                    actionTemplates = routeTemplates;
                }
                else
                {
                    actionTemplates = [string.Empty];
                }

                var httpMethod = HttpAttributes[AttributeName(http)];
                foreach (var controllerTemplate in controllerTemplates)
                {
                    foreach (var actionTemplate in actionTemplates)
                    {
                        var route = Normalize(ReplaceTokens(Combine(controllerTemplate, actionTemplate), controllerName, method.Identifier.Text));
                        if (UnresolvedToken().IsMatch(StripBraces().Replace(route, string.Empty)))
                        {
                            warnings.Add($"{owner}: route '{route}' contains an unsupported token.");
                        }

                        var metadata = extractor.Extract(method, classAttributes, route, owner, warnings);
                        yield return new EndpointDefinition
                        {
                            // Provisional identity (method + route); a stable ID scheme is a later task.
                            Id = $"{httpMethod.ToString().ToUpperInvariant()} {route}",
                            ControllerName = controllerName,
                            ActionName = method.Identifier.Text,
                            HttpMethod = httpMethod,
                            RouteTemplate = route,
                            Parameters = metadata.Parameters,
                            RequestBody = metadata.RequestBody,
                            Authentication = metadata.Authentication,
                            Validation = metadata.Validation
                        };
                    }
                }
            }
        }
    }

    private static bool IsAction(MethodDeclarationSyntax method) =>
        method.Modifiers.Any(SyntaxKind.PublicKeyword)
        && !method.Modifiers.Any(SyntaxKind.StaticKeyword)
        && method.TypeParameterList is null
        && !Attributes(method).Any(a => AttributeName(a) == "NonAction");

    // ---- route handling ----

    private static List<string> ResolveTemplates(IEnumerable<AttributeSyntax> routeAttributes, string owner, List<string> warnings)
    {
        var templates = new List<string>();
        foreach (var attribute in routeAttributes)
        {
            var argument = TemplateArgument(attribute);
            if (argument is null)
            {
                templates.Add(string.Empty);
            }
            else if (TryEvaluate(argument, out var template))
            {
                templates.Add(template);
            }
            else
            {
                warnings.Add($"{owner}: route template is not a string literal and was skipped.");
            }
        }

        return templates.Distinct().ToList();
    }

    private static string Combine(string controllerTemplate, string actionTemplate)
    {
        if (actionTemplate.StartsWith('/') || actionTemplate.StartsWith("~/", StringComparison.Ordinal))
        {
            return actionTemplate;
        }

        if (controllerTemplate.Length == 0)
        {
            return actionTemplate;
        }

        return actionTemplate.Length == 0 ? controllerTemplate : controllerTemplate.TrimEnd('/') + "/" + actionTemplate;
    }

    private static string ReplaceTokens(string route, string controllerName, string actionName) =>
        route.Replace("[controller]", controllerName, StringComparison.OrdinalIgnoreCase)
             .Replace("[action]", actionName, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string route)
    {
        if (route.StartsWith("~/", StringComparison.Ordinal))
        {
            route = route[2..];
        }

        return route.Trim('/');
    }

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex StripBraces();

    [GeneratedRegex(@"\[[A-Za-z_]\w*\]")]
    private static partial Regex UnresolvedToken();

    // ---- syntax helpers ----

    private static bool HasAttribute(ClassGroup group, string name) =>
        group.Declarations.SelectMany(Attributes).Any(a => AttributeName(a) == name);
}
