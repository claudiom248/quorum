using Quorum.Host.Infrastructure;
using Quorum.Host.Infrastructure.Database;
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

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapEndpoints(typeof(Program).Assembly);

await app.RunAsync();

/// <summary>Entry point, public so integration tests can host the app.</summary>
public partial class Program;
