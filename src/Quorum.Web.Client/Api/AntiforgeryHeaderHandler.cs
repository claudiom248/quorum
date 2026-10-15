namespace Quorum.Web.Client.Api;

/// <summary>
/// Sends the header the Host requires on cookie-authenticated calls that change data (CSRF protection): a cross-site
/// page cannot add it without a CORS preflight, which the Host never grants.
/// </summary>
internal sealed class AntiforgeryHeaderHandler : DelegatingHandler
{
    private const string HeaderName = "X-Quorum-Request";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation(HeaderName, "1");
        return base.SendAsync(request, cancellationToken);
    }
}
