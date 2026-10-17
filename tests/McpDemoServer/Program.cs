using Quorum.McpDemoServer;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless = true).WithTools<DemoTools>();

var app = builder.Build();
app.MapMcp("/mcp");
app.Run();
