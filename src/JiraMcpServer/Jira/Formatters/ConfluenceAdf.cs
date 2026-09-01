using System.Text.Json.Nodes;

namespace JiraMcpServer.Jira.Formatters;

/// <summary>
/// Renders Confluence ADF documents to readable plain text. Confluence ADF overlaps with the Jira
/// subset handled by <see cref="Adf"/> (paragraph, headings, lists, code blocks) but adds node
/// types Jira rarely uses (tables, panels, cross-references, media embeds). This renderer
/// understands the Confluence superset and falls back to <see cref="Adf.AdfToText"/> for any node
/// it has not seen, so bound content is never dropped silently.
///
/// A Confluence Atlas Document Format document (<c>body.atlas_doc_format</c>) uses lowercase
/// camelCase types like <c>table</c>/<c>tableRow</c>/<c>tableCell</c> and <c>inlineCard</c>,
/// while the storage-format ADF (<c>body.storage</c>) uses the same node vocabulary. Both are
/// handled here; tables are rendered with a lightweight text layout.
/// </summary>
public static class ConfluenceAdf
{
    /// <summary>Render an ADF document (node object, node array, or already-plain string) to text.</summary>
    public static string AdfToText(object? adf)
    {
        return adf switch
        {
            null or "" => "",
            string s => s,
            JsonArray array => RenderNodes(array),
            JsonObject obj => RenderNode(obj),
            _ => "",
        };
    }

    private static string RenderNodes(JsonArray nodes)
    {
        var blocks = new List<string>();
        foreach (var item in nodes)
        {
            if (item is JsonObject node)
            {
                blocks.Add(RenderNode(node));
            }
        }

        return string.Join("\n\n", blocks.Where(static b => b.Length > 0));
    }

    /// <summary>
    /// Render a single node to a block string. A node may span lines (a table, a multi-row list);
    /// sibling blocks are separated with a blank line by <see cref="RenderNodes"/>.
    /// </summary>
    private static string RenderNode(JsonObject node)
    {
        var kind = node["type"]?.GetValue<string>() ?? "";
        switch (kind)
        {
            case "doc":
                // A full ADF document: render its top-level blocks in sequence.
                return RenderNodes(GetContent(node)).Trim();

            case "text":
                return node["text"]?.GetValue<string>() ?? "";

            case "paragraph":
            case "heading":
                return RenderNodes(GetContent(node)).Trim();

            case "hardBreak":
                return "\n";

            case "bulletList":
            case "orderedList":
            {
                var items = new List<string>();
                var idx = 1;
                foreach (var itemNode in GetContent(node).OfType<JsonObject>())
                {
                    var rendered = RenderNodes(GetContent(itemNode)).Trim();
                    if (rendered.Length == 0)
                    {
                        continue;
                    }

                    var folded = Fold(rendered);
                    items.Add(kind == "bulletList" ? $"- {folded}" : $"{idx}. {folded}");
                    idx++;
                }

                return string.Join("\n", items);
            }

            case "codeBlock":
            {
                var code = string.Concat(GetContent(node)
                    .OfType<JsonObject>()
                    .Where(static n => n["type"]?.GetValue<string>() == "text")
                    .Select(static n => n["text"]?.GetValue<string>() ?? ""));
                return code.Length > 0 ? $"```\n{code}\n```" : "";
            }

            case "blockquote":
            {
                var inner = RenderNodes(GetContent(node)).Trim();
                return inner.Length > 0 ? $"> {inner}" : "";
            }

            case "panel":
            {
                var inner = RenderNodes(GetContent(node)).Trim();
                return inner.Length > 0 ? $"callout: {inner}" : "";
            }

            case "table":
            {
                var rows = new List<string>();
                foreach (var rowNode in GetContent(node).OfType<JsonObject>())
                {
                    var cells = GetContent(rowNode)
                        .OfType<JsonObject>()
                        .Select(cell => Fold(RenderNodes(GetContent(cell))).Trim())
                        .ToList();
                    rows.Add(string.Join(" | ", cells));
                }

                return string.Join("\n", rows);
            }

            // Media nodes: render the alt/caption text if present, otherwise nothing.
            case "mediaSingle":
            case "mediaGroup":
            case "media":
            case "inlineCard":
            case "embedCard":
            {
                var caption = RenderNodeCaption(node);
                return caption.Length > 0 ? "[media] " + caption : "";
            }

            case "mention":
                if (node["attrs"] is JsonObject attrs && attrs["text"]?.GetValue<string>() is { Length: > 0 } mention)
                {
                    return "@" + mention;
                }

                return "";

            case "status":
                if (node["attrs"] is JsonObject statusAttrs && statusAttrs["text"]?.GetValue<string>() is { Length: > 0 } status)
                {
                    return "[" + status + "]";
                }

                return "";

            case "emoticon":
                if (node["attrs"] is JsonObject emoteAttrs && emoteAttrs["shortName"]?.GetValue<string>() is { Length: > 0 } emote)
                {
                    return emote;
                }

                return "";

            default:
            {
                // Unknown node type: fall back to the shared Jira ADF renderer, which descends into
                // children so nothing is lost.
                var rendered = Adf.AdfToText(node).Trim();
                return rendered.Length > 0 ? rendered : "";
            }
        }
    }

    /// <summary>Confluence media nodes carry alt text in <c>attrs.alt</c> or a caption child.</summary>
    private static string RenderNodeCaption(JsonObject node)
    {
        if (node["attrs"] is JsonObject attrs && attrs["alt"]?.GetValue<string>() is { Length: > 0 } alt)
        {
            return alt;
        }

        // mediaSingle may wrap a media child with a caption.
        foreach (var child in GetContent(node).OfType<JsonObject>())
        {
            var rendered = RenderNode(child).Trim();
            if (rendered.Length > 0)
            {
                return rendered;
            }
        }

        return "";
    }

    /// <summary>Fold a multi-line rendered block to a single line for dense structures (tables/lists).</summary>
    private static string Fold(string text) =>
        string.Join(" ", text.Split('\n').Where(static l => l.Trim().Length > 0).Select(static l => l.Trim()));

    private static JsonArray GetContent(JsonObject obj) =>
        obj["content"] as JsonArray ?? new();
}
