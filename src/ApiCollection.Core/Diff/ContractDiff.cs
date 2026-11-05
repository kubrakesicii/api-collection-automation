using ApiCollection.Core.Snapshots;

namespace ApiCollection.Core.Diff;

/// <summary>Compares two sets of contract facts and names every difference.</summary>
public static class ContractDiff
{
    /// <summary>
    /// Facts are grouped by entity. An entity present on one side only is one Added/Removed change summarising its attributes;
    /// an entity on both sides yields one Changed/Added/Removed change per differing attribute. Order facts only count when the
    /// relative order of the items present on both sides differs (adding a parameter is not also a reorder).
    /// Output is ordered by path.
    /// </summary>
    public static IReadOnlyList<ContractChange> Compute(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after)
    {
        var previous = Group(before);
        var current = Group(after);
        var changes = new List<ContractChange>();

        foreach (var entity in previous.Keys.Union(current.Keys).OrderBy(e => e, StringComparer.Ordinal))
        {
            var hasBefore = previous.TryGetValue(entity, out var oldAttributes);
            var hasAfter = current.TryGetValue(entity, out var newAttributes);

            // An order fact only exists when there are two or more items; its appearing or vanishing is just an item being added or removed.
            if (IsOrderEntity(entity) && hasBefore != hasAfter)
            {
                continue;
            }

            if (hasBefore && !hasAfter)
            {
                changes.Add(new ContractChange { Category = CategoryOf(entity, string.Empty), Kind = ChangeKind.Removed, Path = entity, Before = Summarise(oldAttributes!) });
            }
            else if (!hasBefore && hasAfter)
            {
                changes.Add(new ContractChange { Category = CategoryOf(entity, string.Empty), Kind = ChangeKind.Added, Path = entity, After = Summarise(newAttributes!) });
            }
            else
            {
                CompareAttributes(entity, oldAttributes!, newAttributes!, changes);
            }
        }

        return changes;
    }

    private static void CompareAttributes(
        string entity, SortedDictionary<string, string> oldAttributes, SortedDictionary<string, string> newAttributes, List<ContractChange> changes)
    {
        foreach (var attribute in oldAttributes.Keys.Union(newAttributes.Keys).OrderBy(a => a, StringComparer.Ordinal))
        {
            var hasOld = oldAttributes.TryGetValue(attribute, out var oldValue);
            var hasNew = newAttributes.TryGetValue(attribute, out var newValue);
            if (hasOld && hasNew && oldValue == newValue)
            {
                continue;
            }

            if (IsOrder(entity, attribute) && hasOld && hasNew && SameRelativeOrder(oldValue!, newValue!))
            {
                continue;
            }

            changes.Add(new ContractChange
            {
                Category = CategoryOf(entity, attribute),
                Kind = !hasOld ? ChangeKind.Added : !hasNew ? ChangeKind.Removed : ChangeKind.Changed,
                Path = $"{entity}.{attribute}",
                Before = hasOld ? oldValue : null,
                After = hasNew ? newValue : null
            });
        }
    }

    private static Dictionary<string, SortedDictionary<string, string>> Group(IReadOnlyDictionary<string, string> facts)
    {
        var groups = new Dictionary<string, SortedDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (key, value) in facts)
        {
            var (entity, attribute) = EndpointContract.Split(key);
            if (!groups.TryGetValue(entity, out var attributes))
            {
                groups[entity] = attributes = new SortedDictionary<string, string>(StringComparer.Ordinal);
            }

            attributes[attribute] = value;
        }

        return groups;
    }

    private static string Summarise(SortedDictionary<string, string> attributes) =>
        string.Join("; ", attributes.Select(a => $"{a.Key}={a.Value}"));

    private static bool IsOrderEntity(string entity) =>
        entity == "parameters" || entity.StartsWith("body.propertyOrder[", StringComparison.Ordinal);

    private static bool IsOrder(string entity, string attribute) =>
        IsOrderEntity(entity) && attribute is "order" or "names";

    private static bool SameRelativeOrder(string before, string after)
    {
        var oldItems = before.Split(',');
        var newItems = after.Split(',');
        return oldItems.Where(newItems.Contains).SequenceEqual(newItems.Where(oldItems.Contains));
    }

    private static ChangeCategory CategoryOf(string entity, string attribute)
    {
        if (attribute.StartsWith("example", StringComparison.Ordinal)
            || (entity.StartsWith("body.property[", StringComparison.Ordinal) && attribute == "default"))
        {
            return ChangeCategory.Example;
        }

        var root = new string(entity.TakeWhile(c => c is not ('[' or '.')).ToArray());
        return root switch
        {
            "endpoint" => attribute == "route" ? ChangeCategory.Route : ChangeCategory.Naming,
            "parameter" or "parameters" => ChangeCategory.Parameter,
            "body" => ChangeCategory.RequestBody,
            "response" => ChangeCategory.Response,
            "auth" => ChangeCategory.Authentication,
            "validation" => ChangeCategory.Validation,
            _ => ChangeCategory.Other
        };
    }
}
