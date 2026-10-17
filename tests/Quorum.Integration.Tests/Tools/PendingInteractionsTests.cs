using System.Net;
using Microsoft.Extensions.Time.Testing;
using Quorum.Modules.Agents.Tools;
using Quorum.Shared.Results;

namespace Quorum.Integration.Tests.Tools;

/// <summary>The in-memory registry behind confirmations and questions, without a server around it.</summary>
public sealed class PendingInteractionsTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid User = Guid.NewGuid();
    private static readonly Guid Conversation = Guid.NewGuid();

    private static Result Approve(PendingInteractions pending, Guid callId) =>
        pending.Reply(Tenant, User, Conversation, callId, InteractionKind.Confirmation, new InteractionReply(true, null));

    [Fact]
    public async Task ARepliedInteractionCannotBeGivenUpAndKeepsTheReply()
    {
        var pending = new PendingInteractions(new FakeTimeProvider());
        var interaction = pending.Register(Guid.NewGuid(), Tenant, User, Conversation, InteractionKind.Confirmation);

        Assert.True(Approve(pending, interaction.CallId).IsSuccess);
        var cancelled = pending.GiveUp(interaction);

        Assert.False(cancelled);
        Assert.True((await interaction.Reply).Approved);
    }

    [Fact]
    public void AnInteractionGivenUpRefusesALaterReplyAsTooLate()
    {
        var pending = new PendingInteractions(new FakeTimeProvider());
        var interaction = pending.Register(Guid.NewGuid(), Tenant, User, Conversation, InteractionKind.Confirmation);

        Assert.True(pending.GiveUp(interaction));
        var late = Approve(pending, interaction.CallId);

        Assert.False(late.IsSuccess);
        Assert.Equal(ErrorType.Conflict, late.Error.Type);
        Assert.True(interaction.Reply.IsCanceled);
    }

    [Fact]
    public void SettledEntriesAreForgottenAfterTheirRetentionAndOpenOnesAreKept()
    {
        var clock = new FakeTimeProvider();
        var pending = new PendingInteractions(clock);
        var settled = pending.Register(Guid.NewGuid(), Tenant, User, Conversation, InteractionKind.Confirmation);
        var open = pending.Register(Guid.NewGuid(), Tenant, User, Conversation, InteractionKind.Confirmation);
        pending.GiveUp(settled);

        clock.Advance(TimeSpan.FromMinutes(11));
        pending.CloseConversation(Guid.NewGuid());

        Assert.Equal(ErrorType.NotFound, Approve(pending, settled.CallId).Error.Type);
        Assert.True(Approve(pending, open.CallId).IsSuccess);
    }

    [Fact]
    public void AnotherUserTenantOrKindLooksLikeAnUnknownCall()
    {
        var pending = new PendingInteractions(new FakeTimeProvider());
        var interaction = pending.Register(Guid.NewGuid(), Tenant, User, Conversation, InteractionKind.Confirmation);
        var reply = new InteractionReply(true, null);

        Assert.Equal(ErrorType.NotFound, pending.Reply(Tenant, Guid.NewGuid(), Conversation, interaction.CallId, InteractionKind.Confirmation, reply).Error.Type);
        Assert.Equal(ErrorType.NotFound, pending.Reply(Guid.NewGuid(), User, Conversation, interaction.CallId, InteractionKind.Confirmation, reply).Error.Type);
        Assert.Equal(ErrorType.NotFound, pending.Reply(Tenant, User, Guid.NewGuid(), interaction.CallId, InteractionKind.Confirmation, reply).Error.Type);
        Assert.Equal(ErrorType.NotFound, pending.Reply(Tenant, User, Conversation, interaction.CallId, InteractionKind.Question, reply).Error.Type);
        Assert.True(pending.Reply(Tenant, User, Conversation, interaction.CallId, InteractionKind.Confirmation, reply).IsSuccess);
    }
}
