using JiraMcpServer.Configuration;
using JiraMcpServer.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JiraMcpServer.Transport.Stdio;

/// <summary>
/// stdio transport host: the default and recommended mode for local clients (Claude Desktop,
/// Cursor, VS Code). The server is configured entirely from its own process environment (or a
/// config file next to the executable), so the launch command an MCP client runs never contains
/// Jira credentials — they are never exposed to the model executing in the client.
/// </summary>
public static class StdioHost
{
    public static async Task RunAsync(CompiledConfig config)
    {
        var builder = Host.CreateApplicationBuilder();

        // All logs go to stderr; stdout is exclusively the MCP JSON-RPC channel.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(
            console => console.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services.AddJiraServerServices(config);
        builder.Services
            .AddJiraMcpServer()
            .WithStdioServerTransport();

        await builder.Build().RunAsync();
    }
}
