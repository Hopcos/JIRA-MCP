using System.Collections.Concurrent;
using System.Diagnostics;
using JiraMcpServer.Configuration;
using JiraMcpServer.Jira.Safety;
using JiraMcpServer.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JiraMcpServer.Transport.Http;

/// <summary>
/// Options shared by every HTTP host layout: bind address, CORS, and static Bearer auth.
/// </summary>
public sealed record HttpHostOptions
{
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 8080;
    public string Path { get; init; } = "/mcp";
    public IReadOnlyList<string> CorsOrigins { get; init; } = ["*"];
    public string? AuthToken { get; init; }
}

/// <summary>
/// Streamable HTTP host: builds an ASP.NET Core application with the MCP endpoint mapped at the
/// configured path, plus Bearer-token auth, CORS, request logging, and the <c>/health</c> and
/// <c>/</c> helper routes. This is the remote-team-shared mode; a reverse proxy fronts TLS.
/// </summary>
public static class HttpHost
{
    /// <summary>
    /// Build and run the Streamable HTTP host. Blocks until the host is shut down.
    /// </summary>
    public static async Task RunAsync(CompiledConfig config, HttpHostOptions options, ILogger? logger = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(
            console => console.LogToStandardErrorThreshold = Microsoft.Extensions.Logging.LogLevel.Trace);

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(System.Net.IPAddress.Parse(options.Host), options.Port);
        });

        builder.Services.AddJiraServerServices(config);
        builder.Services
            .AddJiraMcpServer()
            .WithHttpTransport(http =>
            {
                // Stateless mode: no session affinity needed; load-balancer friendly.
                http.Stateless = true;
            });

        builder.Services.AddCors(cors =>
        {
            cors.AddDefaultPolicy(policy =>
            {
                var origins = options.CorsOrigins;
                if (origins.Count == 1 && origins[0] == "*")
                {
                    policy.AllowAnyOrigin();
                }
                else
                {
                    policy.WithOrigins(origins.ToArray());
                }

                policy.AllowAnyMethod();
                policy.AllowAnyHeader();
            });
        });

        var app = builder.Build();

        app.UseCors();
        if (!string.IsNullOrEmpty(options.AuthToken))
        {
            app.Use(async (context, next) =>
            {
                var request = context.Request;
                if (!TryReadBearer(request, out var provided) || provided != options.AuthToken)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.Headers["WWW-Authenticate"] = "Bearer realm=\"jira-mcp-server\"";
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = "Unauthorized",
                        detail = "Provide a valid Bearer token via the Authorization header.",
                    });
                    return;
                }

                await next();
            });
        }

        app.Use(async (context, next) =>
        {
            var stopwatch = Stopwatch.StartNew();
            var method = context.Request.Method;
            var path = context.Request.Path.Value ?? "?";
            var auth = context.Request.Headers.Authorization.ToString();
            var masked = string.IsNullOrEmpty(auth) ? "<none>" : Safety.MaskToken(auth);

            try
            {
                await next();
            }
            finally
            {
                stopwatch.Stop();
                var factory = context.RequestServices.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>();
                var logger = factory.CreateLogger("JiraMcpServer.Transport.Http.HttpHost");
                logger.LogInformation("http {Method} {Path} -> {Status} in {Ms}ms (auth={Auth})",
                    method, path, context.Response.StatusCode, stopwatch.Elapsed.TotalMilliseconds.ToString("F1"), masked);
            }
        });

        app.MapMcp(options.Path);

        app.MapGet("/", () =>
                Results.Text(
                    "<html><head><title>Jira MCP Server</title></head><body>" +
                    "<h1>Jira MCP Server</h1>" +
                    "<p>Expose Jira projects, issues, sprints, users, attachments, and worklogs " +
                    "to MCP clients over Streamable HTTP/SSE.</p>" +
                    $"<ul><li>MCP endpoint: <a href=\"{options.Path}\">{options.Path}</a></li>" +
                    "<li>Health check: <a href=\"/health\">/health</a></li></ul>" +
                    "<p>Connect from Claude Code, Claude Desktop, or any MCP client using the " +
                    $"{options.Path} URL.</p></body></html>",
                    "text/html; charset=utf-8"));

        app.MapGet("/health", () => Results.Json(new
        {
            status = "healthy",
            server = "jira-mcp-server",
            version = typeof(ServerSetup).Assembly.GetName().Version?.ToString(3) ?? "0.2.0",
            transport = "http",
            jira_configured = !string.IsNullOrEmpty(config.JiraBaseUrl),
        }));

        logger?.LogInformation("Serving Streamable HTTP on {Host}:{Port}{Path} (client auth: {Auth})",
            options.Host, options.Port, options.Path,
            string.IsNullOrEmpty(options.AuthToken) ? "none" : "Bearer token");

        await app.RunAsync();
    }

    private static bool TryReadBearer(HttpRequest request, out string? token)
    {
        var header = request.Headers.Authorization.ToString();
        var space = header.IndexOf(' ');
        if (space > 0 && header[..space].Equals("Bearer", StringComparison.OrdinalIgnoreCase))
        {
            token = header[(space + 1)..].Trim();
            return token.Length > 0;
        }

        token = null;
        return false;
    }
}
