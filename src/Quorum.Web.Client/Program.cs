using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;
using Quorum.Web.Client;
using Quorum.Web.Client.Api;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Routes>("#app");

builder.Services.AddMudServices();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ThemeState>();
builder.Services.AddTransient<AntiforgeryHeaderHandler>();
builder.Services.AddHttpClient<QuorumApi>(client => client.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress))
    .AddHttpMessageHandler<AntiforgeryHeaderHandler>();
builder.Services.AddScoped<Session>();
builder.Services.AddScoped<OnboardingProgress>();

await builder.Build().RunAsync();
