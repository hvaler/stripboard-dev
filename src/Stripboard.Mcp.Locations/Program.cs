using Stripboard.Infrastructure.Persistence;
using Stripboard.Mcp.Locations.Services;
using Stripboard.Mcp.Locations.Tools;

// mcp-locations: a real Model Context Protocol server (EV-23). See Stripboard.Mcp.Schedule.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddStripboardDatabase(builder.Configuration, "StripboardLocationsMcpDb");
builder.Services.AddScoped<LocationsMcpService>();

builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<LocationsTools>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<StripboardDbContext>();
    await DatabaseRegistration.MigrateAsync(db, app.Logger);
}

app.MapMcp("/mcp");

app.Run();
