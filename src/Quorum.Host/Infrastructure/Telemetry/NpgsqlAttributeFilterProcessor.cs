using System.Diagnostics;
using OpenTelemetry;

namespace Quorum.Host.Infrastructure.Telemetry;

/// <summary>
/// Takes off the spans of Npgsql what must not leave the installation: the text of the SQL command (spec 016 D-8: command text is not
/// captured) and the details of the connection, whose data source holds the host, the port, the database and the user (D-9).
/// It runs when a span ends, before the exporter takes it.
/// </summary>
internal sealed class NpgsqlAttributeFilterProcessor : BaseProcessor<Activity>
{
    private const string NpgsqlSource = "Npgsql";

    private static readonly string[] Removed = ["db.query.text", "db.statement", "db.npgsql.data_source"];

    public override void OnEnd(Activity activity)
    {
        if (activity.Source.Name != NpgsqlSource)
        {
            return;
        }

        foreach (var key in Removed)
        {
            if (activity.GetTagItem(key) is not null)
            {
                // A tag set to null is removed from the activity.
                activity.SetTag(key, null);
            }
        }
    }
}
