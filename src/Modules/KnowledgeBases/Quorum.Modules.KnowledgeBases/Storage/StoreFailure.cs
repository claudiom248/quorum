namespace Quorum.Modules.KnowledgeBases.Storage;

/// <summary>Why a call to a store failed, in the few outcomes every kind maps its errors to (spec 008 D-14).</summary>
internal enum StoreFailure
{
    /// <summary>No connection, or no answer in time.</summary>
    Unreachable,

    /// <summary>The store refused the credentials (or the lack of them).</summary>
    Unauthorized,

    /// <summary>The source (collection, relation) does not exist.</summary>
    NotFound,

    /// <summary>The store answered, but not as the kind expects.</summary>
    Unexpected,

    /// <summary>The store refused the query itself: a vector of the wrong size or a vector that does not exist.</summary>
    Rejected,
}

/// <summary>A call to a store failed. The message is safe to show: it never carries a key, a password or the address.</summary>
internal sealed class StoreException(StoreFailure failure, string message) : Exception(message)
{
    public StoreFailure Failure { get; } = failure;
}
