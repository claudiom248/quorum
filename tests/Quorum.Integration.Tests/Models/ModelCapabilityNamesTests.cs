using Quorum.Modules.Models.Contracts;
using Quorum.Web.Client.Api;

namespace Quorum.Integration.Tests.Models;

/// <summary>The names the server writes are the ones the portal reads (spec 019 D-2): one test keeps the two lists from drifting.</summary>
public sealed class ModelCapabilityNamesTests
{
    [Fact]
    public void TheServerAndThePortalNameEachCapabilityTheSame()
    {
        Assert.Equal([ModelCapabilityNames.Chat], ModelCapabilities.Chat.Names());
        Assert.Equal([ModelCapabilityNames.Tools], ModelCapabilities.Tools.Names());
        Assert.Equal([ModelCapabilityNames.Embeddings], ModelCapabilities.Embeddings.Names());
    }
}
