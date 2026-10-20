using System.Net;
using System.Net.Http.Json;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.Rag;

public sealed class RagToolPermissionTests(PostgresFixture postgres, QdrantFixture qdrant)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-42 (the /tools/rag half)
    public async Task ManagingRagToolsNeedsThePermissionAndAnonymousCallersAreRejected()
    {
        await using var setup = await RagApi.Setup.StartAsync(postgres, qdrant);
        var kb = await setup.RegisterAsync("policies");
        var existing = await RagApi.CreateOkAsync(setup.Admin, RagApi.Body(name: "existing", knowledgeBaseIds: [kb]));
        using var member = await setup.App.CreateMemberClientAsync();
        using var anonymous = setup.App.CreateUiClient();
        var unknown = Guid.NewGuid();

        // What an administrator gets is a success, a 400 for an empty request or a 404 for an unknown tool: never a 401 or 403.
        var requests = new List<(Func<HttpClient, Task<HttpResponseMessage>> Send, HttpStatusCode ForAdmin)>
        {
            (client => client.GetAsync(RagApi.All, Ct), HttpStatusCode.OK),
            (client => client.PostAsJsonAsync(RagApi.All, new { }, Ct), HttpStatusCode.BadRequest),
            (client => client.GetAsync(RagApi.One(existing.Id), Ct), HttpStatusCode.OK),
            (client => client.PutAsJsonAsync(RagApi.One(unknown), new { }, Ct), HttpStatusCode.NotFound),
            (client => client.DeleteAsync(RagApi.One(unknown), Ct), HttpStatusCode.NotFound),
        };

        foreach (var (send, forAdmin) in requests)
        {
            using var asMember = await send(member);
            using var asAnonymous = await send(anonymous);
            using var asAdmin = await send(setup.Admin);

            Assert.Equal(HttpStatusCode.Forbidden, asMember.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, asAnonymous.StatusCode);
            Assert.Equal(forAdmin, asAdmin.StatusCode);
        }

        Assert.Single(await RagApi.ListAsync(setup.Admin));
    }
}
