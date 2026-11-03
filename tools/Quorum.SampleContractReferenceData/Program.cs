using Quorum.SampleContractReferenceData;

// Run:   dotnet run --project tools/Quorum.SampleContractReferenceData
//        (listens on http://127.0.0.1:3002 unless --urls or ASPNETCORE_URLS says otherwise)
//        dotnet run --project tools/Quorum.SampleContractReferenceData -- --require-token <secret>
// Register in Quorum as an MCP server at http://host.docker.internal:3002/mcp (or the token-guarded URL).

if (args.Any(arg => arg is "--help" or "-h" or "/?"))
{
    Console.WriteLine(
        "Quorum sample reference data — a read-only MCP server (Streamable HTTP at /mcp).\n" +
        "Tools: reference_data_summary, fx_rate, price_index, working_days_between, notice_deadline.\n" +
        "Options:\n" +
        "  --urls <url>          where to listen (default http://127.0.0.1:3002)\n" +
        "  --require-token <s>   demand 'Authorization: Bearer <s>' on /mcp; put the same value in the server's registration headers in Quorum\n" +
        "Anything else is passed to ASP.NET Core untouched.");
    return 0;
}

var explicitUrls = args.Any(arg => arg.StartsWith("--urls", StringComparison.OrdinalIgnoreCase))
    || Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is { Length: > 0 };

string? requiredToken = null;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] is "--require-token")
    {
        if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
        {
            Console.Error.WriteLine("--require-token needs a value: the secret the client must send as 'Authorization: Bearer <secret>'.");
            return 2;
        }

        requiredToken = args[++i];
    }
}

var builder = WebApplication.CreateBuilder(args);
if (!explicitUrls)
{
    builder.WebHost.UseUrls("http://127.0.0.1:3002");
}

builder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless = true).WithTools<ReferenceDataTools>();

var app = builder.Build();

if (requiredToken is not null)
{
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/mcp"))
        {
            var header = context.Request.Headers.Authorization.ToString();
            var supplied = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : string.Empty;
            if (!string.Equals(supplied, requiredToken, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                await context.Response.WriteAsync("Missing or wrong bearer token for the reference service.");
                return;
            }
        }

        await next();
    });
}

app.MapMcp("/mcp");

app.MapGet("/", () => Results.Text(
    "Quorum sample reference data — an MCP server. The endpoint is /mcp (Streamable HTTP).\n" +
    "Tools: reference_data_summary, fx_rate, price_index, working_days_between, notice_deadline.\n" +
    "This service reads fixed tables and a calendar; it changes nothing."));

app.Run();
return 0;
