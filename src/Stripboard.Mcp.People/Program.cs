using Stripboard.Infrastructure.Persistence;
using Stripboard.Mcp.People.Services;
using Stripboard.Mcp.People.Tools;

// mcp-people: a real Model Context Protocol server (EV-23). See Stripboard.Mcp.Schedule for
// why "REST endpoints under an /mcp/ path" was not the same thing as speaking MCP.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddStripboardDatabase(builder.Configuration, "StripboardPeopleMcpDb");
builder.Services.AddScoped<PeopleMcpService>();

builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<PeopleTools>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<StripboardDbContext>();
    await DatabaseRegistration.MigrateAsync(db, app.Logger);
}

app.MapMcp("/mcp");

app.Run();
