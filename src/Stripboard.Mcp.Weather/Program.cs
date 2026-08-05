using Stripboard.Mcp.Weather.Services;
using Stripboard.Mcp.Weather.Tools;

// mcp-weather: a real Model Context Protocol server (EV-23). See Stripboard.Mcp.Schedule.
//
// The forecast behind it is synthetic and every response says so — see WeatherTools.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();
builder.Services.AddScoped<WeatherMcpService>();

builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<WeatherTools>();

var app = builder.Build();

app.MapMcp("/mcp");

app.Run();
