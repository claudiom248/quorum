namespace Quorum.Host.Infrastructure;

/// <summary>
/// A client closing the connection mid-response (e.g. stopping a streamed answer) is normal, not a server error:
/// the cancellation it causes ends the request quietly instead of being logged as a 500.
/// </summary>
internal sealed class ClientDisconnectMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Nobody is listening for a response any more.
        }
    }
}
