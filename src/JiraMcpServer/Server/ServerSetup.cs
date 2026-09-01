using JiraMcpServer.Configuration;
using JiraMcpServer.Jira.Client;
using JiraMcpServer.Tools;
using JiraMcpServer.Tools.Attachments;
using JiraMcpServer.Tools.Confluence;
using JiraMcpServer.Tools.Issues;
using JiraMcpServer.Tools.Permissions;
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
using ModelContextProtocol.Protocol;
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
        services.AddScoped(sp => new ConfluenceClient(sp.GetRequiredService<JiraClient>()));
        services.AddScoped(sp => new JiraToolContext
        {
            Client = sp.GetRequiredService<JiraClient>(),
            Confluence = sp.GetRequiredService<ConfluenceClient>(),
            Settings = sp.GetRequiredService<CompiledConfig>(),
            Logger = sp.GetRequiredService<ILogger<JiraToolContext>>(),
        });
        return services;
    }

    /// <summary>
    /// Register the MCP server plus every tool/prompt/resource. Shared by stdio and HTTP hosts.
    /// When <c>JIRA_TOOLS</c> restricts the tool surface, request filters hide disallowed tools
    /// from <c>tools/list</c> and reject <c>tools/call</c> for them — defense in depth so a
    /// deployment scoped to <c>read</c> can never invoke a write tool even if a client bypasses
    /// the listing.
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
            .WithTools<ConfluenceTools>()
            .WithPrompts<PromptTemplates>()
            .WithResources<JiraResources>()
            .WithRequestFilters(filters => filters.AddToolPermissionFilters());
    }

    /// <summary>
    /// Register the two request filters that enforce the configured <c>JIRA_TOOLS</c>
    /// allowlist: <c>ListTools</c> hides disallowed tools, and <c>CallTool</c> rejects calls to
    /// them with an <c>isError</c> result. Both resolve the live <see cref="CompiledConfig"/>
    /// from the request scope, so a token rotation or config reload takes effect without a rebuild.
    /// </summary>
    private static IMcpRequestFilterBuilder AddToolPermissionFilters(this IMcpRequestFilterBuilder builder)
    {
        builder.AddListToolsFilter(next => async (context, cancellationToken) =>
        {
            var result = await next(context, cancellationToken);
            var config = context.Services?.GetService<CompiledConfig>();
            if (config is null || config.AllowAllTools)
            {
                return result;
            }

            result.Tools = result.Tools
                .Where(static tool => tool.Name is not null)
                .Where(tool => config.ToolAllowlist!.Contains(tool.Name!))
                .ToList();
            return result;
        });

        builder.AddCallToolFilter(next => async (context, cancellationToken) =>
        {
            var config = context.Services?.GetService<CompiledConfig>();
            var name = context.Params?.Name;
            if (config is not null && !config.AllowAllTools && name is not null &&
                !config.ToolAllowlist!.Contains(name))
            {
                return new CallToolResult
                {
                    Content = [new TextContentBlock
                    {
                        Text = $"Tool '{name}' is not enabled. The server's JIRA_TOOLS allowlist " +
                            $"[{string.Join(", ", config.ToolAllowlist.OrderBy(static n => n))}] " +
                            $"does not include it; ask the operator to add it or widen the scope.",
                    }],
                    IsError = true,
                };
            }

            return await next(context, cancellationToken);
        });

        return builder;
    }
}
