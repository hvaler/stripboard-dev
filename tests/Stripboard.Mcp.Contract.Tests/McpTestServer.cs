using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Stripboard.Infrastructure.Services;

namespace Stripboard.Mcp.Contract.Tests;

/// <summary>
/// Runs one of our MCP servers over an in-memory pipe and connects a real MCP client to it.
///
/// This exists because the previous "contract" tests called the service classes directly and
/// therefore proved nothing about the protocol — the servers were REST endpoints under an
/// /mcp/ path, no client could have discovered them, and no test would have noticed. What
/// goes through this harness is the real thing: an `initialize` handshake, `tools/list` with
/// generated JSON schemas, `tools/call`, and errors mapped to MCP's error shape.
/// </summary>
public sealed class McpTestServer : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly McpServer _server;
    private readonly Task _running;

    public McpClient Client { get; private set; } = null!;

    private McpTestServer(ServiceProvider services, McpServer server, Task running)
    {
        _services = services;
        _server = server;
        _running = running;
    }

    /// <summary>
    /// A <see cref="CallerIdentityResolver"/> wired to a request that Cloud Run would have
    /// authenticated, so a test can exercise the proven-identity path rather than only the
    /// unverified one. Pass null for "nothing proved anything about this caller".
    /// </summary>
    public static CallerIdentityResolver ResolverFor(string? authenticatedEmail)
    {
        var context = new DefaultHttpContext();
        if (authenticatedEmail is not null)
        {
            // IAP's header, which is the shape a human Producer arrives in.
            context.Request.Headers["X-Goog-Authenticated-User-Email"] =
                $"accounts.google.com:{authenticatedEmail}";
        }

        return new CallerIdentityResolver(
            new HttpContextAccessor { HttpContext = context },
            logger: null,
            behindAuthenticatingPlatform: true);
    }

    /// <summary>Builds the server exactly as Program.cs does: DI, then WithTools&lt;T&gt;.</summary>
    public static async Task<McpTestServer> StartAsync<TTools>(Action<IServiceCollection> configure)
        where TTools : class
    {
        var collection = new ServiceCollection();
        configure(collection);
        collection.AddMcpServer().WithTools<TTools>();

        var services = collection.BuildServiceProvider();
        var options = services.GetRequiredService<IOptions<McpServerOptions>>().Value;

        Pipe clientToServer = new(), serverToClient = new();

        var server = McpServer.Create(
            new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()),
            options,
            serviceProvider: services);

        var running = server.RunAsync();

        var harness = new McpTestServer(services, server, running);
        harness.Client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()));

        return harness;
    }

    public async Task<IList<McpClientTool>> ListToolsAsync() => await Client.ListToolsAsync();

    /// <summary>
    /// Calls a tool and returns its structured result as JSON.
    ///
    /// A tool that fails does not produce a protocol error: MCP returns an ordinary result
    /// with <c>isError: true</c> and the reason as text, so the model can read it and try
    /// something else. That distinction is the whole reason our tools throw
    /// <see cref="McpException"/> rather than letting an exception escape — an escaped
    /// exception is a broken server; an <c>isError</c> result is an answer. This surfaces it
    /// as an exception so a test can assert on the message.
    /// </summary>
    public async Task<JsonElement> CallAsync(string name, object? arguments = null)
    {
        var args = arguments is null
            ? new Dictionary<string, object?>()
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(arguments))!;

        var result = await Client.CallToolAsync(name, args);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));

        if (result.IsError is true)
        {
            throw new McpException(text);
        }

        if (result.StructuredContent is { } structured)
        {
            return structured;
        }

        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "null" : text).RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await _server.DisposeAsync();
        try { await _running; } catch { /* the transport closes underneath it; that is the shutdown */ }
        await _services.DisposeAsync();
    }
}
