using System.Net;
using System.Net.Http.Json;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.KnowledgeBases;

public sealed class KnowledgeBasePermissionTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-42 (the /knowledge-bases half)
    public async Task ManagingTheDictionaryNeedsThePermissionAndAnonymousCallersAreRejected()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        using var member = await app.CreateMemberClientAsync();
        using var anonymous = app.CreateUiClient();

        var fieldId = await DictionaryApi.CreateOkAsync(admin, "existing");

        var requests = new List<Func<HttpClient, Task<HttpResponseMessage>>>
        {
            client => client.GetAsync(DictionaryApi.Dictionary, Ct),
            client => client.PostAsJsonAsync(DictionaryApi.Dictionary, new { name = "created", type = "Text", scope = "Tenant" }, Ct),
            client => client.PutAsJsonAsync(DictionaryApi.Field(fieldId), new { description = "changed" }, Ct),
            // Last: the administrator's call removes the field.
            client => client.DeleteAsync(DictionaryApi.Field(fieldId), Ct),
        };

        foreach (var send in requests)
        {
            using var asMember = await send(member);
            using var asAnonymous = await send(anonymous);
            using var asAdmin = await send(admin);

            Assert.Equal(HttpStatusCode.Forbidden, asMember.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, asAnonymous.StatusCode);
            Assert.True(asAdmin.IsSuccessStatusCode, $"The administrator was refused: {(int)asAdmin.StatusCode}");
        }
    }

    [Fact] // AC-42 (the /knowledge-bases half)
    public async Task ManagingKnowledgeBasesNeedsThePermissionAndAnonymousCallersAreRejected()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        using var member = await app.CreateMemberClientAsync();
        using var anonymous = app.CreateUiClient();
        var id = Guid.NewGuid();

        // Incomplete or unknown on purpose: a caller who may manage gets a 400 or 404, and nothing is contacted.
        var requests = new List<Func<HttpClient, Task<HttpResponseMessage>>>
        {
            client => client.GetAsync(KnowledgeBaseApi.All, Ct),
            client => client.PostAsJsonAsync(KnowledgeBaseApi.All, new { kind = "Qdrant" }, Ct),
            client => client.GetAsync(KnowledgeBaseApi.One(id), Ct),
            client => client.PutAsJsonAsync(KnowledgeBaseApi.One(id), new { kind = "Qdrant" }, Ct),
            client => client.DeleteAsync(KnowledgeBaseApi.One(id), Ct),
            client => client.PostAsync(KnowledgeBaseApi.Verify(id), content: null, Ct),
            client => client.PostAsJsonAsync(Search.SearchApi.Search(id), new { query = "leave" }, Ct),
        };

        foreach (var send in requests)
        {
            using var asMember = await send(member);
            using var asAnonymous = await send(anonymous);
            using var asAdmin = await send(admin);

            Assert.Equal(HttpStatusCode.Forbidden, asMember.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, asAnonymous.StatusCode);
            Assert.NotEqual(HttpStatusCode.Forbidden, asAdmin.StatusCode);
            Assert.NotEqual(HttpStatusCode.Unauthorized, asAdmin.StatusCode);
        }
    }

    [Fact] // AC-42 (the /knowledge-bases half)
    public async Task InspectingNeedsThePermissionAndAnonymousCallersAreRejected()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        using var member = await app.CreateMemberClientAsync();
        using var anonymous = app.CreateUiClient();

        // The request is incomplete on purpose: a caller who may inspect gets a 400, and nothing is contacted.
        var body = new { kind = "Qdrant" };
        using var asMember = await member.PostAsJsonAsync(InspectApi.Inspect, body, Ct);
        using var asAnonymous = await anonymous.PostAsJsonAsync(InspectApi.Inspect, body, Ct);
        using var asAdmin = await admin.PostAsJsonAsync(InspectApi.Inspect, body, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, asMember.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, asAnonymous.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, asAdmin.StatusCode);
    }
}
