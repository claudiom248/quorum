using System.Diagnostics;
using OpenTelemetry;

namespace Quorum.Host.Infrastructure.Telemetry;

/// <summary>
/// Takes off the spans of Npgsql what must not leave the installation: the connection details, whose data source holds the host, the port,
/// the database and the user (spec 016 D-9). The command text stays: every query of Quorum is parameterized, so it holds placeholders and no
/// values (D-8, amended). It runs when a span ends, before the exporter takes it.
/// </summary>
internal sealed class NpgsqlAttributeFilterProcessor : BaseProcessor<Activity>
{
    private const string NpgsqlSource = "Npgsql";

    private static readonly string[] Removed = ["db.statement", "db.npgsql.data_source"];

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
