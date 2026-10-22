namespace Quorum.Modules.KnowledgeBases.Storage;

/// <summary>
/// An optional operation a kind may support besides the required core of <see cref="IStoreAdapter"/> (spec 008 A1-1). A consumer
/// asks the adapter for it with <see cref="IStoreAdapter.Capability{T}"/> and treats <see langword="null"/> as "not supported",
/// never as an error to find out about by calling it.
/// </summary>
internal interface IStoreCapability;
