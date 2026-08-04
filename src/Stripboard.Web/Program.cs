using Microsoft.EntityFrameworkCore;
using Stripboard.Infrastructure.Persistence;
using Stripboard.Mcp.Schedule.Services;
using Stripboard.CallSheets.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

builder.Services.AddDbContext<StripboardDbContext>(options =>
    options.UseInMemoryDatabase("StripboardWebDb"));
builder.Services.AddScoped<ScheduleMcpService>();
builder.Services.AddSingleton<CallSheetPdfGenerator>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapRazorPages();
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();
