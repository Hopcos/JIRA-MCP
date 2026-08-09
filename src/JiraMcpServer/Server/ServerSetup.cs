using JiraMcpServer.Configuration;
using JiraMcpServer.Jira.Client;
using JiraMcpServer.Tools;
using JiraMcpServer.Tools.Attachments;
using JiraMcpServer.Tools.Issues;
using JiraMcpServer.Tools.Projects;
using JiraMcpServer.Tools.Prompts;
using JiraMcpServer.Tools.Resources;
using JiraMcpServer.Tools.Sprints;
using JiraMcpServer.Tools.Users;
using JiraMcpServer.Tools.Worklog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace JiraMcpServer.Server;

/// <summary>
/// Shared wiring for every transport: registers the compiled configuration, the Jira client,
/// the per-request tool context, and every MCP tool/prompt/resource. Both the stdio host and the
/// Streamable HTTP host call <see cref="ConfigureMcp"/> so the tool surface is identical regardless
/// of how the server is launched.
/// </summary>
public static class ServerSetup
{
    /// <summary>
    /// Register the shared services (settings, Jira client, tool context) on the container.
    /// </summary>
    public static IServiceCollection AddJiraServerServices(this IServiceCollection services, CompiledConfig config)
    {
        services.AddSingleton(config);
        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<JiraClient>>();
            return new JiraClient(config, logger: logger);
        });
        services.AddScoped(sp => new JiraToolContext
        {
            Client = sp.GetRequiredService<JiraClient>(),
            Settings = sp.GetRequiredService<CompiledConfig>(),
            Logger = sp.GetRequiredService<ILogger<JiraToolContext>>(),
        });
        return services;
    }

    /// <summary>
    /// Register the MCP server plus every tool/prompt/resource. Shared by stdio and HTTP hosts.
    /// </summary>
    public static IMcpServerBuilder AddJiraMcpServer(
        this IServiceCollection services,
        Action<McpServerOptions>? configure = null)
    {
        return services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new ModelContextProtocol.Protocol.Implementation
                {
                    Name = "jira-mcp-server",
                    Version = typeof(ServerSetup).Assembly.GetName().Version?.ToString(3) ?? "0.2.0",
                };
                options.Capabilities = new ModelContextProtocol.Protocol.ServerCapabilities
                {
                    Tools = new ModelContextProtocol.Protocol.ToolsCapability(),
                    Prompts = new ModelContextProtocol.Protocol.PromptsCapability(),
                };
                configure?.Invoke(options);
            })
            .WithTools<IssueTools>()
            .WithTools<ProjectTools>()
            .WithTools<SprintTools>()
            .WithTools<UserTools>()
            .WithTools<AttachmentTools>()
            .WithTools<WorklogTools>()
            .WithPrompts<PromptTemplates>()
            .WithResources<JiraResources>();
    }
}
