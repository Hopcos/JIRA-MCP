using System.CommandLine;
using JiraMcpServer.Configuration;
using JiraMcpServer.Jira.Safety;
using JiraMcpServer.Transport.Http;
using JiraMcpServer.Transport.Stdio;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace JiraMcpServer.Cli;

/// <summary>Version of the server, mirrored from the assembly version.</summary>
public static class AppVersion
{
    public static string Current =>
        typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "0.2.0";
}

/// <summary>
/// Command-line entry point. Flags take precedence over environment variables (which are read by
/// <see cref="ConfigLoader"/>). <c>--transport</c> selects stdio (default) or http (Streamable HTTP).
///
/// Security: Jira credentials (<c>JIRA_API_TOKEN</c>, <c>JIRA_USER_EMAIL</c>) and the base URL are
/// server-side only — resolved from an auto-discovered config file next to the server, the process
/// environment, or a <c>.env</c> file. They are never accepted on the command line, because doing so
/// would expose the token to the model running in the client. HTTP client-connection auth uses
/// <c>--auth-mode token</c> + <c>--server-token</c> and is unrelated to the Jira credentials.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var transportOption = new Option<string>("--transport")
        {
            Description = "Transport: stdio, or http (Streamable HTTP, legacy SSE-compatible).",
            HelpName = "Transport",
            DefaultValueFactory = _ => "stdio",
        };

        var tokenFileOption = new Option<string?>("--token-file")
        {
            Description = "Path to a credentials file (KEY=VALUE) holding JIRA_BASE_URL / JIRA_API_TOKEN, " +
                "so secrets sit next to the installation instead of client config; exposed to this process only.",
        };

        var searchEngineOption = new Option<string>("--search-engine")
        {
            Description = "Issue search API: jql (default, enhanced POST /rest/api/3/search/jql), get " +
                "(GET /rest/api/3/search/jql), or auto (alias for jql). The old /rest/api/3/search " +
                "is removed by Jira Cloud (410).",
            DefaultValueFactory = _ => "jql",
        };

        var hostOption = new Option<string>("--host")
        {
            Description = "HTTP bind host.",
            HelpName = "Host",
            DefaultValueFactory = _ => "127.0.0.1",
        };

        var portOption = new Option<int>("--port")
        {
            Description = "HTTP bind port.",
            HelpName = "Port",
            DefaultValueFactory = _ => 8080,
        };

        var authModeOption = new Option<string>("--auth-mode")
        {
            Description = "HTTP auth mode: none | token.",
            HelpName = "Mode",
            DefaultValueFactory = _ => "none",
        };

        var serverTokenOption = new Option<string?>("--server-token")
        {
            Description = "Bearer token clients must send in HTTP mode.",
            HelpName = "Token",
        };

        var corsOption = new Option<string>("--cors-origins")
        {
            Description = "Comma-separated CORS origins.",
            HelpName = "Origins",
            DefaultValueFactory = _ => "*",
        };

        var logLevelOption = new Option<string>("--log-level")
        {
            Description = "Logging level: DEBUG, INFO, WARNING, ERROR.",
            HelpName = "Level",
            DefaultValueFactory = _ => "INFO",
        };

        var versionOption = new Option<bool>("--version")
        {
            Description = "Print version and exit.",
        };

        var root = new RootCommand("Jira MCP Server: expose the Jira REST API to MCP clients over stdio or HTTP.")
        {
            transportOption,
            tokenFileOption,
            searchEngineOption,
            hostOption,
            portOption,
            authModeOption,
            serverTokenOption,
            corsOption,
            logLevelOption,
            versionOption,
        };

        // RootCommand ships a built-in --version that prints the bare assembly version; replace it
        // with one that matches the server's branding ("jira-mcp-server 0.2.0").
        var builtinVersion = root.Options.FirstOrDefault(o => o.Name == "--version");
        if (builtinVersion is not null)
        {
            root.Options.Remove(builtinVersion);
        }

        root.SetAction(async parseResult =>
        {
            var transport = parseResult.GetValue(transportOption) ?? "stdio";
            var tokenFile = parseResult.GetValue(tokenFileOption);
            var searchEngine = parseResult.GetValue(searchEngineOption) ?? "jql";
            var host = parseResult.GetValue(hostOption) ?? "127.0.0.1";
            var port = parseResult.GetValue(portOption);
            var authMode = parseResult.GetValue(authModeOption) ?? "none";
            var serverToken = parseResult.GetValue(serverTokenOption);
            var cors = parseResult.GetValue(corsOption) ?? "*";
            var logLevel = parseResult.GetValue(logLevelOption) ?? "INFO";
            var version = parseResult.GetValue(versionOption);

            if (version)
            {
                Console.WriteLine($"jira-mcp-server {AppVersion.Current}");
                return 0;
            }

            if (transport is not ("stdio" or "http"))
            {
                await Console.Error.WriteLineAsync($"Invalid --transport '{transport}'; choose stdio or http.");
                return 2;
            }

            if (authMode is not ("none" or "token"))
            {
                await Console.Error.WriteLineAsync($"Invalid --auth-mode '{authMode}'; choose none or token.");
                return 2;
            }

            try
            {
                // Credentials are server-side only. A token file is merged into the process environment
                // BEFORE settings are built (mirroring the Python --token-file behaviour).
                if (tokenFile is not null)
                {
                    EnvironmentVariableLoader.MergeTokenFile(tokenFile);
                }

                // The CLI --search-engine flag is an explicit override: when given, it wins over
                // appsettings.json, jira_server.toml, and JIRA_SEARCH_ENGINE. When omitted, the
                // configured value is used as-is.
                var searchOverride = ConfigLoader.BuildSearchEngineOverride(
                    searchEngine, parseResult.Tokens.Select(static t => t.Value));

                var config = ConfigLoader.Load(searchOverride);
                var rootLogger = CreateLogger(logLevel);
                rootLogger.LogInformation(
                    "Starting jira-mcp-server v{Version}, transport={Transport}, jira={Jira}, auth={Auth} (jira token {Token})",
                    AppVersion.Current, transport, MaskUrl(config.JiraBaseUrl), config.AuthMethod,
                    Safety.MaskToken(config.ApiToken));

                if (transport == "stdio")
                {
                    await StdioHost.RunAsync(config);
                    return 0;
                }

                // HTTP mode.
                if (authMode == "token" && string.IsNullOrEmpty(serverToken))
                {
                    rootLogger.LogWarning(
                        "--auth-mode=token but no --server-token set; client connections will be rejected.");
                }

                var effectiveToken = authMode == "token" && !string.IsNullOrEmpty(serverToken) ? serverToken : null;
                await HttpHost.RunAsync(config, new HttpHostOptions
                {
                    Host = host,
                    Port = port,
                    CorsOrigins = ParseCors(cors),
                    AuthToken = effectiveToken,
                }, rootLogger);
                return 0;
            }
            catch (ConfigException exc)
            {
                await Console.Error.WriteLineAsync($"Configuration error: {exc.Message}");
                return 1;
            }
            catch (Exception exc)
            {
                await Console.Error.WriteLineAsync($"Fatal error: {exc.Message}");
                return 1;
            }
        });

        var parseResult = root.Parse(args);
        if (parseResult.Errors.Count > 0)
        {
            foreach (var error in parseResult.Errors)
            {
                await Console.Error.WriteLineAsync(error.Message);
            }

            return 2;
        }

        return await parseResult.InvokeAsync(parseResult.InvocationConfiguration, default);
    }

    /// <summary>A minimal stderr logger for the CLI itself.</summary>
    private static ILogger CreateLogger(string level)
    {
        var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(ParseLevel(level));
            builder.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);
        });
        return factory.CreateLogger("jira-mcp-server");
    }

    private static LogLevel ParseLevel(string level) =>
        level.ToUpperInvariant() switch
        {
            "DEBUG" => LogLevel.Debug,
            "WARNING" or "WARN" => LogLevel.Warning,
            "ERROR" => LogLevel.Error,
            _ => LogLevel.Information,
        };

    private static string MaskUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return "<not set>";
        }

        var space = url.IndexOf("://", StringComparison.Ordinal);
        if (space < 0)
        {
            return "<invalid>";
        }

        var scheme = url[..space];
        var rest = url[(space + 3)..];
        var hostEnd = rest.IndexOfAny(['/', '?']);
        var host = hostEnd < 0 ? rest : rest[..hostEnd];
        // Strip userinfo (@) if any path carries it.
        var at = host.LastIndexOf('@');
        if (at >= 0)
        {
            host = host[(at + 1)..];
        }

        return $"{scheme}://{host}";
    }

    private static IReadOnlyList<string> ParseCors(string cors) =>
        string.IsNullOrWhiteSpace(cors)
            ? ["*"]
            : cors.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// Merges the <c>KEY=VALUE</c> entries of a token file into the process environment using
/// setdefault semantics, so files provisioned next to the installation work without environment
/// variables. This mirrors the Python <c>--token-file</c> behaviour.
/// </summary>
public static class EnvironmentVariableLoader
{
    public static void MergeTokenFile(string tokenFile)
    {
        if (!File.Exists(tokenFile))
        {
            throw new ConfigException($"Token file not found: {tokenFile}");
        }

        foreach (var rawLine in File.ReadLines(tokenFile))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim().Trim('"', '\'');
            if (value.Length > 0)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }
}
