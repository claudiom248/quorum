namespace Quorum.Modules.KnowledgeBases.Contracts;

/// <summary>
/// Says whether anything the knowledge base module stores has changed since a caller last looked, so a caller that keeps what it
/// read (the knowledge base catalog, the lookups) knows when to read it again.
/// </summary>
public interface IKnowledgeBaseChangeStamp
{
    /// <summary>A number that grows whenever the module's data is written, whichever way it was written. Equal numbers mean no change in between.</summary>
    long Current { get; }
}
