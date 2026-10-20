using Microsoft.EntityFrameworkCore;

namespace Quorum.Modules.KnowledgeBases.Persistence;

/// <summary>
/// Serialises the two things that cannot be checked by the database: a knowledge base saving a mapping that names dictionary
/// fields, and a dictionary field being deleted. The mapped ids live in a JSON column, so no foreign key stops a delete from
/// racing a save. Both take this lock, then read what they depend on, so whichever comes second sees the first's result.
/// </summary>
internal static class DictionaryLock
{
    // One key for the whole dictionary: saving a knowledge base and deleting a field are rare, and one lock is easy to reason about.
    private const long Key = 0x4B42_4449_4354;

    /// <summary>
    /// Waits for its turn. Released at commit or rollback of the current transaction, which must be open and keep the default
    /// READ COMMITTED isolation: the reads after this call have to see what the previous holder committed.
    /// </summary>
    public static Task AcquireAsync(KnowledgeBasesDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({Key})", cancellationToken);
    }
}
