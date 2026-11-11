namespace Quorum.Modules.Models.Contracts;

/// <summary>
/// The consumers of models of one kind (agents, knowledge bases). Models asks them so it can refuse a change that would break a
/// consumer (spec 019 D-10, D-11, D-12) without referencing those modules.
/// </summary>
public interface IModelUsageSource
{
    Task<IReadOnlyList<ModelUse>> UsesOfAsync(Guid modelId, CancellationToken cancellationToken);
}

/// <param name="Kind">The kind shown in a conflict: <c>agent</c> or <c>knowledge_base</c>.</param>
/// <param name="Required">The capabilities this consumer relies on.</param>
public sealed record ModelUse(string Kind, Guid Id, string Name, ModelUseScope Scope, ModelCapabilities Required);

public static class ModelUseKinds
{
    public const string Agent = "agent";

    public const string KnowledgeBase = "knowledge_base";
}
