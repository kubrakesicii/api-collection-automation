using System.Text;
using ApiCollection.Core.Models;

namespace ApiCollection.Core.Identity;

/// <summary>
/// Stable endpoint identity: HTTP method plus a canonical route (see docs/adr/0001-endpoint-identity.md).
/// Controller and action names are deliberately not part of the identity, so refactoring them never makes an endpoint look new.
/// Changing the rules below requires bumping <see cref="Version"/>.
/// </summary>
public static class EndpointIdentity
{
    /// <summary>Version of the canonicalization rules; snapshots made with another version cannot be compared.</summary>
    public const int Version = 1;

    /// <summary>
    /// Creates the identity, for example <c>GET /api/products/{:int}</c> for <c>[HttpGet("{id:int}")]</c> on <c>api/Products</c>.
    /// </summary>
    public static string Create(HttpMethodKind method, string routeTemplate) =>
        $"{method.ToString().ToUpperInvariant()} /{CanonicalRoute(routeTemplate)}";

    /// <summary>
    /// Canonicalizes a human-written identity such as <c>get api/Products/{id:int}</c>. Text that does not start with
    /// an HTTP method is returned trimmed and unchanged, so it simply fails to match anything.
    /// </summary>
    public static string Canonicalize(string identityText)
    {
        var text = identityText.Trim();
        var space = text.IndexOfAny([' ', '\t']);
        if (space <= 0)
        {
            return text;
        }

        var token = text[..space];
        return token.All(char.IsLetter) && Enum.TryParse<HttpMethodKind>(token, ignoreCase: true, out var method)
            ? Create(method, text[(space + 1)..])
            : text;
    }

    /// <summary>
    /// Rules: leading "~/" and "/" and trailing "/" are dropped, repeated slashes collapse, literal segments are lower-cased
    /// (ASP.NET Core routing is case-insensitive), and every route parameter keeps only its shape:
    /// the name and default value are dropped, constraints (lower-cased names, arguments verbatim), "?" and "*" are kept.
    /// </summary>
    public static string CanonicalRoute(string route)
    {
        var text = route.Trim();
        if (text.StartsWith("~/", StringComparison.Ordinal))
        {
            text = text[2..];
        }

        var result = new StringBuilder();
        var index = 0;
        while (index < text.Length)
        {
            var current = text[index];
            if (current == '{')
            {
                var end = FindClosingBrace(text, index);
                if (end < 0)
                {
                    // Malformed template: keep the rest as it is so two malformed routes still compare by text.
                    result.Append(text[index..].ToLowerInvariant());
                    break;
                }

                result.Append(CanonicalParameter(text[(index + 1)..end]));
                index = end + 1;
            }
            else
            {
                if (current != '/' || (result.Length > 0 && result[^1] != '/'))
                {
                    result.Append(char.ToLowerInvariant(current));
                }

                index++;
            }
        }

        return result.ToString().TrimEnd('/');
    }

    private static string CanonicalParameter(string inner)
    {
        var stars = new StringBuilder();
        var text = inner;
        while (text.StartsWith('*') && stars.Length < 2)
        {
            stars.Append('*');
            text = text[1..];
        }

        var equals = TopLevelIndex(text, '=');
        if (equals >= 0)
        {
            text = text[..equals];
        }

        var optional = text.EndsWith('?');
        if (optional)
        {
            text = text[..^1];
        }

        // The name is dropped but its slot stays (":int"), so canonical text parses back to itself: canonicalizing twice is a no-op.
        var constraints = SplitTopLevel(text, ':').Skip(1).Select(c => ":" + CanonicalConstraint(c));
        return "{" + stars + string.Concat(constraints) + (optional ? "?" : string.Empty) + "}";
    }

    private static string CanonicalConstraint(string constraint)
    {
        var trimmed = constraint.Trim();
        var open = trimmed.IndexOf('(');
        return open < 0 ? trimmed.ToLowerInvariant() : trimmed[..open].ToLowerInvariant() + trimmed[open..];
    }

    private static int FindClosingBrace(string text, int start)
    {
        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}' && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static int TopLevelIndex(string text, char target)
    {
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is '(' or '{')
            {
                depth++;
            }
            else if (text[i] is ')' or '}')
            {
                depth--;
            }
            else if (text[i] == target && depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static List<string> SplitTopLevel(string text, char separator)
    {
        var parts = new List<string>();
        var rest = text;
        int at;
        while ((at = TopLevelIndex(rest, separator)) >= 0)
        {
            parts.Add(rest[..at]);
            rest = rest[(at + 1)..];
        }

        parts.Add(rest);
        return parts;
    }
}
