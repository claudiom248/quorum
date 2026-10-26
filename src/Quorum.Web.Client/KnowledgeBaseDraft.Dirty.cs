namespace Quorum.Web.Client;

public sealed partial class KnowledgeBaseDraft
{
    private string _opened = string.Empty;

    /// <summary>Something the admin typed or chose differs from what the draft was opened with: leaving the wizard asks first.</summary>
    public bool IsDirty => !string.Equals(_opened, Snapshot(), StringComparison.Ordinal);

    /// <summary>Takes the draft as it is now for the starting point of <see cref="IsDirty"/>.</summary>
    public KnowledgeBaseDraft MarkOpened()
    {
        _opened = Snapshot();
        return this;
    }

    // The connection, the scope and whether a collection was inspected: what the admin types, not what the server proposes.
    private string Snapshot() => string.Join(
        '\u001f',
        Name,
        Description,
        Url,
        ApiKey,
        Collection,
        VectorName,
        EmbeddingModel,
        QueryPrefix,
        Schema,
        LookupRefreshHours,
        Scope.Kind,
        Scope.TenantsText,
        string.Join(',', Scope.AssistantIds.Order()),
        Mapping is not null,
        Kind,
        UseConnectionString,
        Host,
        Port,
        Database,
        Username,
        Password,
        SslMode,
        ConnectionString,
        DatabaseSchema,
        Table,
        VectorColumn,
        Distance);
}
