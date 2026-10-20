var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddMcpServer()
    .WithHttpTransport();

var app = builder.Build();

app.MapMcp("/mcp");

app.Run();
