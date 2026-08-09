using JiraMcpServer.Jira.Safety;
using JiraMcpServer.Tools.Permissions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;

namespace JiraMcpServer.Configuration;

/// <summary>
/// Compile-time validation result for a configuration snapshot.
/// Mirrors the Python <c>Settings</c> contract and its fail-fast validation.
/// </summary>
public sealed class CompiledConfig
{
    public required string JiraBaseUrl { get; init; }

    public required string AuthMethod { get; init; }

    public string? UserEmail { get; init; }

    public string? ApiToken { get; init; }

    public string? ProjectKeys { get; init; }

    public string? Tools { get; init; }

    public required string SearchEngine { get; init; }

    public int RateLimit { get; init; } = 100;

    public double RequestTimeout { get; init; } = 30;

    public double ConnectTimeout { get; init; } = 10;

    /// <summary>Project keys allowed for writes; empty means unrestricted.</summary>
    public IReadOnlyList<string> ConfiguredProjectKeys =>
        string.IsNullOrWhiteSpace(ProjectKeys)
            ? Array.Empty<string>()
            : ProjectKeys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static key => key.ToUpperInvariant())
                .ToArray();

    public bool AllowAllProjects => ConfiguredProjectKeys.Count == 0;

    /// <summary>
    /// Resolved tool allowlist. <c>null</c> means every tool is enabled (no <c>tools</c>
    /// configured); a non-empty set means only those tool names may be listed or invoked.
    /// </summary>
    public IReadOnlySet<string>? ToolAllowlist { get; init; }

    /// <summary>True when no tool restriction is configured, i.e. every tool is enabled.</summary>
    public bool AllowAllTools => ToolAllowlist is null || ToolAllowlist.Count == 0;

    /// <summary>The (method, path) pair for issue search, resolved from <see cref="SearchEngine"/>.</summary>
    public (string Method, string Path) SearchApiPaths =>
        SearchEngine == "get"
            ? ("GET", "/rest/api/3/search/jql")
            : ("POST", "/rest/api/3/search/jql");
}

/// <summary>
/// Builds and validates a <see cref="CompiledConfig"/> from the layered configuration
/// (auto-discovered TOML/JSON file next to the server, <c>JIRA_*</c> environment variables).
/// Validation happens synchronously at startup so an invalid configuration fails fast rather
/// than half-working.
/// </summary>
public static class ConfigLoader
{
    private const string DiscoveryFileName = "jira_server";
    private const string AppSettingsFileName = "appsettings.json";

    /// <summary>
    /// Build configuration in precedence order (highest wins):
    ///   1. <c>appsettings.json</c> next to the server or in the working directory;
    ///   2. Auto-discovered <c>jira_server.toml</c>/<c>jira_server.json</c>;
    ///   3. <c>JIRA_*</c> environment variables;
    ///   4. Explicit caller overrides (e.g. <c>--search-engine</c> from the CLI).
    /// The file providers use keys without the <c>JIRA_</c> prefix.
    /// </summary>
    public static CompiledConfig Load(IConfiguration? overrides = null)
    {
        var builder = new ConfigurationBuilder();

        var appSettings = DiscoverAppSettings();
        if (appSettings is not null)
        {
            builder.AddJsonFile(appSettings, optional: false, reloadOnChange: false);
        }

        var configFile = DiscoverConfigFile();
        if (configFile is not null)
        {
            var extension = Path.GetExtension(configFile);
            if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                builder.AddJsonFile(configFile, optional: false, reloadOnChange: false);
            }
            else if (extension.Equals(".toml", StringComparison.OrdinalIgnoreCase))
            {
                AddTomlFileRaw(builder, configFile);
            }
            else
            {
                throw new ConfigException(
                    $"Unsupported config file format for {configFile}: use .toml or .json");
            }
        }

        builder.AddEnvironmentVariables("JIRA_");

        if (overrides is not null)
        {
            builder.AddConfiguration(overrides);
        }

        var config = builder.Build();
        return Compile(config);
    }

    /// <summary>
    /// Build an optional override provider from an explicitly-given CLI <c>--search-engine</c>
    /// value. Returns <c>null</c> when the flag was not passed, so the configured value
    /// (appsettings.json / jira_server.toml / <c>JIRA_SEARCH_ENGINE</c>) is left untouched.
    /// </summary>
    public static IConfiguration? BuildSearchEngineOverride(
        string searchEngine,
        IEnumerable<string> cliTokens)
    {
        var explicitlyGiven = cliTokens.Any(static token => token == "--search-engine");
        if (!explicitlyGiven)
        {
            return null;
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["search_engine"] = searchEngine,
            })
            .Build();
    }

    /// <summary>
    /// Search for <c>appsettings.json</c> next to the server: first the process working directory
    /// (where the MCP client launched the server), then the directory of the executable.
    /// </summary>
    private static string? DiscoverAppSettings()
        => DiscoverFile(AppSettingsFileName);

    /// <summary>
    /// Search for <c>jira_server.toml</c> / <c>jira_server.json</c> next to the server: first the
    /// process working directory (where the MCP client launched the server), then the directory of
    /// the executable, so a bare launch command finds the file no matter which cwd is used.
    /// </summary>
    public static string? DiscoverConfigFile()
    {
        var toml = DiscoverFile(DiscoveryFileName + ".toml");
        if (toml is not null)
        {
            return toml;
        }

        return DiscoverFile(DiscoveryFileName + ".json");
    }

    private static string? DiscoverFile(string fileName)
    {
        var candidates = new List<string> { Environment.CurrentDirectory };

        var executablePath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(executablePath))
        {
            var exeDir = Path.GetDirectoryName(executablePath);
            if (!string.IsNullOrEmpty(exeDir))
            {
                candidates.Add(exeDir);
            }
        }

        foreach (var dir in candidates.Distinct())
        {
            var candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Compile a raw <see cref="IConfiguration"/> into validated settings. Exposed separately
    /// so tests can build configurations entirely in-memory.
    /// </summary>
    public static CompiledConfig Compile(IConfiguration config)
    {
        var baseUrl = NormalizeUrl(config["base_url"]);
        var authMethod = (config["auth_method"] ?? "basic").Trim().ToLowerInvariant();
        var userEmail = Safety.NormalizeCredential(config["user_email"]);
        var apiToken = Safety.NormalizeCredential(config["api_token"]);
        var projectKeys = Safety.NormalizeCredential(config["project_keys"]);
        var tools = Safety.NormalizeCredential(config["tools"]);

        if (string.IsNullOrEmpty(baseUrl))
        {
            throw new ConfigException(
                "JIRA_BASE_URL is not set: the server-side environment must provide the Jira instance URL. " +
                "Configure it via JIRA_BASE_URL (in the server's .env or environment), a jira_server.toml " +
                "next to the server, or --token-file.");
        }

        if (authMethod is not ("basic" or "bearer"))
        {
            throw new ConfigException($"JIRA_AUTH_METHOD must be 'basic' or 'bearer', got '{authMethod}'.");
        }

        if (authMethod == "basic" && string.IsNullOrEmpty(userEmail))
        {
            throw new ConfigException("JIRA_USER_EMAIL is required when JIRA_AUTH_METHOD=basic.");
        }

        if (string.IsNullOrEmpty(apiToken))
        {
            throw new ConfigException($"JIRA_API_TOKEN is required when JIRA_AUTH_METHOD={authMethod}.");
        }

        var searchEngine = NormalizeSearchEngine(config["search_engine"]);
        var rateLimit = ParseInt(config["rate_limit"], 100, "JIRA_RATE_LIMIT");
        if (rateLimit < 1)
        {
            throw new ConfigException("JIRA_RATE_LIMIT must be >= 1.");
        }

        var requestTimeout = ParseDouble(config["request_timeout"], 30, "JIRA_REQUEST_TIMEOUT");
        var connectTimeout = ParseDouble(config["connect_timeout"], 10, "JIRA_CONNECT_TIMEOUT");

        // Fail fast: a misspelled keyword/tool must abort startup, not drop tools. ParseTools
        // also expands category keywords (read/create/update/delete/write) into concrete tool
        // names, yielding the allowlist the request filters enforce at tools/list and tools/call.
        var toolAllowlist = string.IsNullOrEmpty(tools) ? null : Permissions.ParseTools(tools);

        return new CompiledConfig
        {
            JiraBaseUrl = baseUrl,
            AuthMethod = authMethod,
            UserEmail = userEmail,
            ApiToken = apiToken,
            ProjectKeys = projectKeys,
            Tools = tools,
            ToolAllowlist = toolAllowlist,
            SearchEngine = searchEngine,
            RateLimit = rateLimit,
            RequestTimeout = requestTimeout,
            ConnectTimeout = connectTimeout,
        };
    }

    private static string? NormalizeUrl(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (!trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new ConfigException(
                $"JIRA_BASE_URL must use https:// (got '{value}'). Plaintext http URLs are refused for " +
                "security; use HTTPS (or https://localhost/127.0.0.1 for local proxy TLS termination).");
        }

        return trimmed.TrimEnd('/');
    }

    private static string NormalizeSearchEngine(string? value)
    {
        var normalized = (value ?? "jql").Trim().ToLowerInvariant();
        return normalized switch
        {
            "jql" or "auto" => "jql",
            "get" => "get",
            _ => throw new ConfigException(
                $"JIRA_SEARCH_ENGINE must be 'jql', 'get', or 'auto'; got '{value}'."),
        };
    }

    private static int ParseInt(string? value, int defaultValue, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !int.TryParse(value, out var parsed))
        {
            return defaultValue;
        }

        return parsed;
    }

    private static double ParseDouble(string? value, double defaultValue, string name)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return defaultValue;
        }

        return parsed;
    }

    private static void AddTomlFileRaw(IConfigurationBuilder builder, string configFile)
    {
        string raw;
        try
        {
            raw = File.ReadAllText(configFile);
        }
        catch (Exception exc)
        {
            throw new ConfigException($"Failed to read config file {configFile}: {exc.Message}");
        }

        Tomlyn.Model.TomlTable? table;
        try
        {
            table = Tomlyn.TomlSerializer.Deserialize<Tomlyn.Model.TomlTable>(raw);
        }
        catch (Exception exc)
        {
            throw new ConfigException($"Invalid TOML config file {configFile}: {exc.Message}");
        }

        if (table is null)
        {
            throw new ConfigException($"Config file {configFile} must contain a table, got null.");
        }

        var result = new Dictionary<string, string?>();
        FlattenTable(table!, "", result, configFile);
        builder.AddInMemoryCollection(result);
    }

    private static void FlattenTable(
        Tomlyn.Model.TomlTable table, string prefix, Dictionary<string, string?> output, string path)
    {
        foreach (var (key, value) in table)
        {
            var fullKey = string.IsNullOrEmpty(prefix) ? key : $"{prefix}{ConfigurationPath.KeyDelimiter}{key}";
            switch (value)
            {
                case Tomlyn.Model.TomlTable nested:
                    FlattenTable(nested, fullKey, output, path);
                    break;
                case string s:
                    output[fullKey] = s;
                    break;
                case bool b:
                    output[fullKey] = b ? "true" : "false";
                    break;
                case long l:
                    output[fullKey] = l.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case int i:
                    output[fullKey] = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case double d:
                    output[fullKey] = d.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case null:
                    break;
                default:
                    throw new ConfigException(
                        $"Unsupported TOML config type for key '{fullKey}' in {path}: {value.GetType().Name}.");
            }
        }
    }
}

/// <summary>Raised when server-side configuration is invalid or missing required values.</summary>
public sealed class ConfigException : Exception
{
    public ConfigException(string message)
        : base(message)
    {
    }
}
