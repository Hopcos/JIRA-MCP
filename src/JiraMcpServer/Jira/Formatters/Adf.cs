using System.Text.Json;
using System.Text.Json.Nodes;

namespace JiraMcpServer.Jira.Formatters;

/// <summary>
/// Pure helpers to convert Atlassian Document Format (ADF) to plain text, and plain text to a
/// minimal ADF document. The MCP tools accept plain text, auto-convert it to ADF before sending,
/// and render descriptions back to readable plain text for the model. Unknown node types are
/// rendered by descending into their children, so content is never lost silently.
/// </summary>
public static class Adf
{
    /// <summary>
    /// Convert plain text into a minimal Jira ADF document. Single paragraphs become one
    /// <c>paragraph</c> node; consecutive <c>\n\n</c> separated sections become multiple paragraphs.
    /// </summary>
    public static System.Text.Json.Nodes.JsonObject TextToAdf(string? text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return EmptyAdfDocument();
        }

        var paragraphs = trimmed.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var content = new System.Text.Json.Nodes.JsonArray();
        foreach (var paragraph in paragraphs)
        {
            content.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "paragraph",
                ["content"] = new System.Text.Json.Nodes.JsonArray
                {
                    new System.Text.Json.Nodes.JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = paragraph,
                    },
                },
            });
        }

        return new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = "doc",
            ["version"] = 1,
            ["content"] = content,
        };
    }

    public static System.Text.Json.Nodes.JsonObject EmptyAdfDocument() =>
        new()
        {
            ["type"] = "doc",
            ["version"] = 1,
            ["content"] = new System.Text.Json.Nodes.JsonArray
            {
                new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "paragraph",
                    ["content"] = new System.Text.Json.Nodes.JsonArray
                    {
                        new System.Text.Json.Nodes.JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = "",
                        },
                    },
                },
            },
        };

    /// <summary>Flatten an ADF document (or bare node list) to readable plain text.</summary>
    public static string AdfToText(object? adf)
    {
        return adf switch
        {
            null or "" => "",
            string s => s,
            System.Text.Json.Nodes.JsonArray array => RenderNodes(array),
            System.Text.Json.Nodes.JsonObject obj => ConvertToText(obj),
            _ => "",
        };
    }

    private static string ConvertToText(System.Text.Json.Nodes.JsonObject obj)
    {
        if (obj["type"]?.GetValue<string>() == "text" && obj["text"]?.GetValue<string>() is { Length: > 0 } plain)
        {
            return plain;
        }

        return RenderNodes(GetContent(obj));
    }

    /// <summary>Render a description that is either ADF JSON text or plain text.</summary>
    public static string AdfTextFromMaybeJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "";
        }

        try
        {
            var node = JsonNode.Parse(raw);
            if (node is System.Text.Json.Nodes.JsonObject obj && obj["type"]?.GetValue<string>() == "doc")
            {
                return AdfToText(obj);
            }
        }
        catch (JsonException)
        {
            // Not JSON; fall through to plain text.
        }

        return raw;
    }

    private static System.Text.Json.Nodes.JsonArray GetContent(System.Text.Json.Nodes.JsonObject obj) =>
        obj["content"] as System.Text.Json.Nodes.JsonArray ?? new();

    /// <summary>Render an iterable of ADF nodes into a flat string of text.</summary>
    private static string RenderNodes(System.Text.Json.Nodes.JsonArray nodes)
    {
        var blocks = new List<string>();
        foreach (var item in nodes)
        {
            if (item is not System.Text.Json.Nodes.JsonObject node)
            {
                continue;
            }

            var nodeType = node["type"]?.GetValue<string>() ?? "";
            switch (nodeType)
            {
                case "text":
                    if (node["text"]?.GetValue<string>() is { Length: > 0 } text)
                    {
                        blocks.Add(text);
                    }

                    break;

                case "paragraph":
                    var inner = RenderNodes(GetContent(node)).Trim();
                    if (inner.Length > 0)
                    {
                        blocks.Add(inner);
                    }

                    break;

                case "bulletList":
                    foreach (var itemNode in node["content"] as System.Text.Json.Nodes.JsonArray ?? new())
                    {
                        if (itemNode is not System.Text.Json.Nodes.JsonObject child)
                        {
                            continue;
                        }

                        var rendered = RenderNodes(GetContent(child));
                        var folded = string.Join(" ", rendered.Split('\n').Where(static l => l.Trim().Length > 0).Select(static l => l.Trim()));
                        if (folded.Length > 0)
                        {
                            blocks.Add($"- {folded}");
                        }
                    }

                    break;

                case "orderedList":
                {
                    var idx = 1;
                    foreach (var itemNode in node["content"] as System.Text.Json.Nodes.JsonArray ?? new())
                    {
                        if (itemNode is not System.Text.Json.Nodes.JsonObject child)
                        {
                            continue;
                        }

                        var rendered = RenderNodes(GetContent(child));
                        var folded = string.Join(" ", rendered.Split('\n').Where(static l => l.Trim().Length > 0).Select(static l => l.Trim()));
                        if (folded.Length > 0)
                        {
                            blocks.Add($"{idx}. {folded}");
                        }

                        idx++;
                    }

                    break;
                }

                case "codeBlock":
                {
                    var code = string.Concat((node["content"] as System.Text.Json.Nodes.JsonArray ?? new())
                        .OfType<System.Text.Json.Nodes.JsonObject>()
                        .Where(static n => n["type"]?.GetValue<string>() == "text")
                        .Select(static n => n["text"]?.GetValue<string>() ?? ""));
                    if (code.Length > 0)
                    {
                        blocks.Add($"```\n{code}\n```");
                    }

                    break;
                }

                default:
                    blocks.Add(RenderNodes(GetContent(node)));
                    break;
            }
        }

        return string.Join("\n\n", blocks.Where(static b => b.Length > 0));
    }

    /// <summary>
    /// Return a valid ADF document for any supported description input: an existing ADF document
    /// (passed through), a string (converted), or <c>null</c> (returns the empty ADF doc).
    /// </summary>
    public static System.Text.Json.Nodes.JsonObject NormalizeDescription(object? value)
    {
        switch (value)
        {
            case null:
                return EmptyAdfDocument();
            case System.Text.Json.Nodes.JsonObject obj when obj["type"]?.GetValue<string>() == "doc":
                return obj;
            case System.Text.Json.Nodes.JsonObject obj when obj["content"] is not null:
                return obj;
            case string s when TryParse(s, out var parsed) && parsed["type"]?.GetValue<string>() == "doc":
                return parsed;
            case string s:
                return TextToAdf(s);
            default:
                return TextToAdf(value.ToString());
        }
    }

    private static bool TryParse(string text, out System.Text.Json.Nodes.JsonObject obj)
    {
        try
        {
            var node = JsonNode.Parse(text);
            if (node is System.Text.Json.Nodes.JsonObject parsed)
            {
                obj = parsed;
                return true;
            }
        }
        catch (JsonException)
        {
            // fall through
        }

        obj = null!;
        return false;
    }
}
