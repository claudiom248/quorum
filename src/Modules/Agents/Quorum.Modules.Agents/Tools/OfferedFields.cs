using Quorum.Modules.KnowledgeBases.Contracts;

namespace Quorum.Modules.Agents.Tools;

/// <summary>The filter fields of a rag or lookup tool as the model is offered them.</summary>
internal static class OfferedFields
{
    // The model knows the field by its alias. A field with no description of its own says which cluster it is from, so two fields that
    // share a name (hr/year, finance/year) are not told apart only by their aliases.
    public static FilterFieldInfo Offered(FilterFieldInfo field, string? alias) => field with
    {
        Name = alias ?? field.Name,
        Description = field.Description ?? (field.Cluster is null ? null : $"{field.Name} ({field.Cluster})"),
    };

    /// <summary>The first field offered under each name, so two that share one keep the one that sorts first.</summary>
    public static IReadOnlyDictionary<string, T> FirstByName<T>(IEnumerable<FilterFieldInfo> fields, Func<FilterFieldInfo, T> value) =>
        fields.GroupBy(field => field.Name, StringComparer.Ordinal).ToDictionary(group => group.Key, group => value(group.First()), StringComparer.Ordinal);
}
