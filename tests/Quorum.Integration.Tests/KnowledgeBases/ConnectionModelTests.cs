using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Quorum.Modules.KnowledgeBases.Persistence;

namespace Quorum.Integration.Tests.KnowledgeBases;

/// <summary>
/// The connection of a knowledge base moved from an owned JSON type to a value converter over the same <c>jsonb</c> column
/// (spec 008 R5): the model snapshot changed, the table must not.
/// </summary>
public sealed class ConnectionModelTests
{
    [Fact]
    public void TheModelHasNoChangesThatAMigrationStillHasToApply()
    {
        using var db = new KnowledgeBasesDbContextDesignFactory().CreateDbContext([]);

        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public void TheMigrationThatMovedTheConnectionChangesNothingInTheDatabase()
    {
        var migrationType = typeof(KnowledgeBasesDbContext).Assembly.GetTypes()
            .Single(type => type.GetCustomAttribute<MigrationAttribute>()?.Id.EndsWith("_ConnectionSettingsAsJson", StringComparison.Ordinal) == true);

        var migration = (Migration)Activator.CreateInstance(migrationType)!;

        Assert.Empty(migration.UpOperations);
        Assert.Empty(migration.DownOperations);
    }
}
