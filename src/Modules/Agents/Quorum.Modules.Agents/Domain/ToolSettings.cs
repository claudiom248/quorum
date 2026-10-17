namespace Quorum.Modules.Agents.Domain;

/// <summary>A tenant's limits for the tool-calling loop and the audit log. One row per tenant.</summary>
internal sealed class ToolSettings
{
    public const int DefaultMaxRounds = 8;
    public const int DefaultTotalTimeoutSeconds = 300;
    public const int DefaultCallTimeoutSeconds = 30;
    public const int DefaultConfirmationTimeoutSeconds = 300;
    public const int DefaultMaxResultCharacters = 20_000;
    public const int DefaultAuditRetentionDays = 90;

    private ToolSettings()
    {
    }

    public Guid TenantId { get; private set; }

    public int MaxRounds { get; private set; }

    public int TotalTimeoutSeconds { get; private set; }

    public int CallTimeoutSeconds { get; private set; }

    public int ConfirmationTimeoutSeconds { get; private set; }

    public int MaxResultCharacters { get; private set; }

    public int AuditRetentionDays { get; private set; }

    public static ToolSettings CreateDefault(Guid tenantId) => new()
    {
        TenantId = tenantId,
        MaxRounds = DefaultMaxRounds,
        TotalTimeoutSeconds = DefaultTotalTimeoutSeconds,
        CallTimeoutSeconds = DefaultCallTimeoutSeconds,
        ConfirmationTimeoutSeconds = DefaultConfirmationTimeoutSeconds,
        MaxResultCharacters = DefaultMaxResultCharacters,
        AuditRetentionDays = DefaultAuditRetentionDays,
    };

    public void Change(int maxRounds, int totalTimeoutSeconds, int callTimeoutSeconds, int confirmationTimeoutSeconds, int maxResultCharacters, int auditRetentionDays)
    {
        MaxRounds = maxRounds;
        TotalTimeoutSeconds = totalTimeoutSeconds;
        CallTimeoutSeconds = callTimeoutSeconds;
        ConfirmationTimeoutSeconds = confirmationTimeoutSeconds;
        MaxResultCharacters = maxResultCharacters;
        AuditRetentionDays = auditRetentionDays;
    }
}
