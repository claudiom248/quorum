using Quorum.Host.Infrastructure;
using Quorum.Host.Infrastructure.Database;
using Quorum.Modules.Agents;
using Quorum.Modules.Conversations;
using Quorum.Modules.Tenancy;
using Quorum.Shared.Endpoints;
using Scalar.AspNetCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog((services, logger) => logger
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddQuorumDataProtection(builder.Configuration);
builder.Services.AddPlatformDatabase(builder.Configuration);

// Module order is startup order: each module's database initializers run in registration order.
builder.Services.AddTenancyModule();
builder.Services.AddAgentsModule();
builder.Services.AddConversationsModule();

var app = builder.Build();

app.UseExceptionHandler();
app.UseBlazorFrameworkFiles();
app.UseStaticFiles(StaticAssetCaching.Options);
app.UseStatusCodePages();
app.UseSerilogRequestLogging();
app.UseMiddleware<ClientDisconnectMiddleware>();
app.UseAuthentication();
app.UseMiddleware<AntiforgeryHeaderMiddleware>();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapEndpoints(typeof(Program).Assembly, TenancyModule.Assembly, AgentsModule.Assembly, ConversationsModule.Assembly);

// The web UI is a single-page app: every non-API path that is not a file falls back to its entry page.
app.MapFallback("/api/{**path}", () => TypedResults.NotFound());
app.MapFallbackToFile("index.html", StaticAssetCaching.Options);

await app.RunAsync();

/// <summary>Entry point, public so integration tests can host the app.</summary>
public partial class Program;
