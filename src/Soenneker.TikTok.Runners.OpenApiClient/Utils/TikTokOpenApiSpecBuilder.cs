using Soenneker.Playwrights.Crawler.Abstract;
using Soenneker.Playwrights.Crawler.Dtos;
using System.Collections.Concurrent;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Soenneker.AngleSharp.Parser.Abstract;
using Soenneker.Utils.File.Abstract;
using Microsoft.Playwright;
using Soenneker.OpenApi.Fixer.Abstract;
using Soenneker.TikTok.Runners.OpenApiClient.Utils.Abstract;

namespace Soenneker.TikTok.Runners.OpenApiClient.Utils;

public sealed class TikTokOpenApiSpecBuilder(IAngleSharpParser angleSharpParser, IPlaywrightCrawler crawler) : ITikTokOpenApiSpecBuilder
{
    private const string DocsBase = "https://developers.tiktok.com/docs/en/";
    private sealed record Table(string[] Section, List<string[]> Rows);

    public async ValueTask<JsonObject> Build(CancellationToken cancellationToken = default)
    {
        var documents = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        string[] slugs = [];
        PlaywrightCrawlOptions discovery = CaptureOptions();
        discovery.Url = DocsBase;
        discovery.ReadinessExpression = "() => !!window._ROUTER_DATA?.loaderData?.['docs/(lang)/layout']?.tree";
        discovery.PageCompletedHandler = async (page, result, token) =>
        {
            ValidatePage(result);
            slugs = await page.EvaluateAsync<string[]>("""
            () => {
                const tree = window._ROUTER_DATA?.loaderData?.['docs/(lang)/layout']?.tree;
                const flatten = nodes => (nodes ?? []).flatMap(n => [n, ...flatten(n.children)]);
                const products = flatten(tree).filter(n => n.title === 'Content Posting API');
                if (products.length !== 1) throw new Error('Documentation product navigation changed');
                const references = flatten(products[0].children).filter(n => n.title === 'API Reference');
                if (references.length !== 1) throw new Error('API reference navigation changed');
                return flatten(references[0].children).filter(n => n.type === 'doc').map(n => n.slug);
            }
            """).WaitAsync(token);
        };
        await crawler.Crawl(discovery, cancellationToken);
        if (slugs.Length == 0) throw new InvalidOperationException("No official reference articles discovered.");
        PlaywrightCrawlOptions references = CaptureOptions();
        references.StartingUrls = slugs.Distinct(StringComparer.Ordinal).Select(slug => DocsBase + slug).ToList();
        references.ReadinessExpression = "() => !!document.querySelector(\"article[class*='docRenderer'] table\")";
        references.PageCompletedHandler = async (page, result, token) =>
        {
            ValidatePage(result);
            string slug = new Uri(result.RequestedUrl).Segments.Last().TrimEnd('/');
            if (new Uri(result.FinalUrl).AbsolutePath.TrimEnd('/') != "/docs/en/" + slug)
                throw new InvalidOperationException($"Official article redirected to an unexpected document: {slug}");
            ILocator article = page.Locator("article[class*='docRenderer']");
            await article.Locator("table").First.WaitForAsync(new() { State = WaitForSelectorState.Visible }).WaitAsync(token);
            string content = await article.EvaluateAsync<string>("""
                element => {
                    const copy = element.cloneNode(true);
                    copy.querySelectorAll('a.anchor-link, button').forEach(node => node.remove());
                    return copy.innerHTML;
                }
                """).WaitAsync(token);
            if (!documents.TryAdd(slug, content)) throw new InvalidOperationException($"Duplicate article: {slug}");
        };
        await crawler.Crawl(references, cancellationToken);
        if (documents.Count != references.StartingUrls.Count) throw new InvalidOperationException("Not all discovered articles were captured.");
        return await BuildFromDocuments(documents, cancellationToken);
    }

    private static PlaywrightCrawlOptions CaptureOptions() => new()
    {
        SaveToDisk = false,
        DiscoverLinks = false,
        UseStealth = false,
        ContinueOnPageError = false,
        NavigationTimeoutMs = 60_000
    };

    private static void ValidatePage(PlaywrightCrawlPageResult result)
    {
        if (result.StatusCode is not (>= 200 and < 300) || result.IsChallengePage)
            throw new InvalidOperationException($"Unable to capture official documentation: {result.RequestedUrl} (HTTP {result.StatusCode})");
    }

    public async ValueTask<JsonObject> BuildFromDocuments(IReadOnlyDictionary<string, string> documents, CancellationToken cancellationToken = default)
    {
        HtmlParser parser = await angleSharpParser.Get(cancellationToken);
        var paths = new JsonObject();
        var schemas = new JsonObject();
        var sources = new JsonArray();
        var allTables = new Dictionary<string, List<Table>>(StringComparer.Ordinal);
        foreach ((string slug, string content) in documents)
            allTables.Add(slug, await ReadTables(parser, content, cancellationToken));
        foreach ((string slug, string content) in documents.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<Table> tables = allTables[slug];
            Table metadata = tables.Single(t => t.Rows.Any(r => r.Length == 2 && r[0] == "HTTP URL" && r[1].StartsWith('/')));
            string path = metadata.Rows.Single(r => r[0] == "HTTP URL")[1];
            string method = metadata.Rows.Single(r => r[0] == "HTTP Method")[1].ToLowerInvariant();
            if (!new[] { "get", "post", "put", "patch", "delete", "head", "options" }.Contains(method))
                throw new InvalidOperationException($"Unsupported HTTP method in {slug}: {method}");
            string name = string.Concat(Regex.Matches(method + " " + path, "[A-Za-z0-9]+").Select(m => char.ToUpperInvariant(m.Value[0]) + m.Value[1..]));
            string url = DocsBase + slug;
            sources.Add(new JsonObject { ["url"] = url, ["sha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant() });
            using var html = await parser.ParseDocumentAsync(content, cancellationToken);
            List<(string Section, string Code)> examples = ReadExamples(html);
            JsonObject? request = ReadContract(tables, examples, "Request", allTables.Values.SelectMany(t => t).ToList());
            JsonObject response = ReadContract(tables, examples, "Response", tables)
                ?? throw new InvalidOperationException($"Missing response contract: {slug}");
            schemas.Add(name + "Response", response);
            var responses = new JsonObject
            {
                ["200"] = Response(name + "Response", "Inspect error.code; an HTTP 200 response can still contain an API error.")
            };
            Table? errorTable = tables.SingleOrDefault(t => t.Rows.Count > 0 && t.Rows[0].Length >= 2 &&
                t.Rows[0][0].StartsWith("HTTP Status", StringComparison.OrdinalIgnoreCase));
            foreach (IGrouping<string, string[]> group in (errorTable?.Rows.Skip(1) ?? []).Where(r => r.Length >= 3 && r[0].Length > 0)
                         .GroupBy(r => Regex.Match(r[0], @"\b(?:[1-5]\d\d|[1-5][xX]{2})\b").Value.ToUpperInvariant()))
            {
                if (group.Key.Length == 0)
                    throw new InvalidOperationException($"Unrecognized HTTP status in {slug}.");
                string description = string.Join("\n", group.Select(r => r[1] + ": " + r[2]));
                if (group.Key == "200")
                    responses["200"]!["description"] = "Inspect error.code. " + description;
                else
                    responses[group.Key] = Response(name + "Response", description);
            }
            var operation = new JsonObject
            {
                ["operationId"] = name,
                ["summary"] = name,
                ["externalDocs"] = new JsonObject { ["url"] = url },
                ["x-tiktok-scopes"] = metadata.Rows.FirstOrDefault(r => r.Length == 2 && r[0] == "Scope")?[1],
                ["responses"] = responses
            };
            if (request != null)
            {
                
                schemas.Add(name + "Request", request);
                operation["requestBody"] = new JsonObject
                {
                    ["required"] = true,
                    ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = Ref(name + "Request") } }
                };
            }
            if (paths[path] is not JsonObject pathItem) paths[path] = pathItem = new JsonObject();
            pathItem.Add(method, operation);
        }
        if (paths.Count == 0) throw new InvalidOperationException("No endpoint contracts found.");
        return new JsonObject
        {
            ["openapi"] = "3.0.3",
            ["info"] = new JsonObject { ["title"] = "TikTok Developer Content Posting API", ["version"] = "1.0.0",
                ["description"] = "Derived by Soenneker from official TikTok documentation. Not an official TikTok OpenAPI release. Covers posting, draft upload, creator info and status; OAuth, Display, Research, webhooks and binary upload URLs are outside this document." },
            ["servers"] = new JsonArray(new JsonObject { ["url"] = "https://open.tiktokapis.com" }),
            ["security"] = new JsonArray(new JsonObject { ["BearerAuth"] = new JsonArray() }),
            ["paths"] = paths,
            ["components"] = new JsonObject { ["schemas"] = schemas, ["securitySchemes"] = new JsonObject
                { ["BearerAuth"] = new JsonObject { ["type"] = "http", ["scheme"] = "bearer" } } },
            ["x-documentation-sources"] = sources
        };
    }

    private static JsonObject Ref(string name) => new() { ["$ref"] = "#/components/schemas/" + name };
    private static JsonObject Response(string schema, string description) => new()
    {
        ["description"] = description,
        ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = Ref(schema) } }
    };
    private static JsonObject ObjectSchema() => new() { ["type"] = "object", ["properties"] = new JsonObject() };
    private static void AddProperty(JsonObject target, string name, JsonObject schema, bool required = false)
    {
        ((JsonObject)target["properties"]!).Add(name, schema);
        if (required)
        {
            if (target["required"] is not JsonArray) target["required"] = new JsonArray();
            ((JsonArray)target["required"]!).Add(name);
        }
    }

    private static JsonObject ParseFields(Table table, List<Table> tables, HashSet<string>? resolving = null)
    {
        string[] header = table.Rows[0];
        int typeIndex = Array.IndexOf(header, "Type");
        int descriptionIndex = Array.IndexOf(header, "Description");
        int requiredIndex = Array.IndexOf(header, "Required");
        bool nested = header.Length > 1 && header[1].StartsWith("Nested Field", StringComparison.Ordinal);
        if (typeIndex < 0 || descriptionIndex < 0) throw new InvalidOperationException("Unrecognized field table.");
        var result = ObjectSchema();
        foreach (string[] row in table.Rows.Skip(1))
        {
            if (row.All(string.IsNullOrWhiteSpace)) continue;
            if (row.Length != header.Length) throw new InvalidOperationException("Field table column count changed.");
            string name = row[nested ? 1 : 0].Trim();
            if (!Regex.IsMatch(name, @"^[a-z][a-z0-9_]*$")) throw new InvalidOperationException($"Unrecognized field: {name}");
            JsonObject schema = TypeSchema(row[typeIndex], tables, resolving);
            schema["description"] = row[descriptionIndex];
            string required = requiredIndex < 0 ? "" : row[requiredIndex];
            bool unconditional = required.Equals("true", StringComparison.OrdinalIgnoreCase) && !Regex.IsMatch(row[descriptionIndex], "only works|only required|required when", RegexOptions.IgnoreCase);
            if (required.Length > 0) schema["x-tiktok-required"] = required;
            JsonObject parent = result;
            if (nested)
            {
                string group = row[0];
                if (!Regex.IsMatch(group, @"^[a-z][a-z0-9_]*$")) throw new InvalidOperationException($"Unrecognized parent: {group}");
                if (result["properties"]![group] is null) AddProperty(result, group, ObjectSchema(), requiredIndex >= 0);
                parent = (JsonObject)result["properties"]![group]!;
            }
            // Rowspans repeat whole cells (e.g. status descriptions). Accept exact repeats only.
            if (parent["properties"]![name] is JsonNode previous)
            {
                if (!JsonNode.DeepEquals(previous, schema)) throw new InvalidOperationException($"Conflicting field: {name}");
                continue;
            }
            AddProperty(parent, name, schema, unconditional);
        }
        if (((JsonObject)result["properties"]!).Count == 0) throw new InvalidOperationException("Empty field table.");
        return result;
    }

    private static JsonObject TypeSchema(string type, List<Table> tables, HashSet<string>? resolving = null)
    {
        type = type.Trim().ToLowerInvariant();
        if (type.StartsWith("list<", StringComparison.Ordinal) && type.EndsWith('>'))
            return new JsonObject { ["type"] = "array", ["items"] = TypeSchema(type[5..^1], tables, resolving) };
        Table? definition = tables.SingleOrDefault(t => string.Equals(t.Section.LastOrDefault(), type, StringComparison.OrdinalIgnoreCase) && IsFieldTable(t));
        if (definition != null)
        {
            resolving ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!resolving.Add(type)) throw new InvalidOperationException($"Cyclic object definition: {type}");
            JsonObject result = ParseFields(definition, tables, resolving);
            resolving.Remove(type);
            return result;
        }
        return type switch
        {
            "string" => new JsonObject { ["type"] = "string" },
            "bool" or "boolean" => new JsonObject { ["type"] = "boolean" },
            "int" or "int32" or "integer" => new JsonObject { ["type"] = "integer", ["format"] = "int32" },
            "int64" => new JsonObject { ["type"] = "integer", ["format"] = "int64" },
            _ => throw new InvalidOperationException($"Unsupported documented type: {type}")
        };
    }

    private static async ValueTask<List<Table>> ReadTables(HtmlParser parser, string content, CancellationToken cancellationToken)
    {
        using var html = await parser.ParseDocumentAsync(content, cancellationToken);
        var headings = new SortedDictionary<int, string>();
        var result = new List<Table>();
        foreach (IElement node in html.All)
        {
            if (Regex.IsMatch(node.LocalName, "^h[1-6]$"))
            {
                int level = node.LocalName[1] - '0';
                foreach (int key in headings.Keys.Where(k => k >= level).ToArray()) headings.Remove(key);
                headings.Add(level, Text(node));
            }
            if (node.LocalName != "table") continue;
            var rows = new List<string[]>();
            var spans = new Dictionary<int, (string Text, int Remaining)>();
            foreach (IElement tr in node.QuerySelectorAll("tr").Where(n => n.ParentElement?.Closest("table") == node))
            {
                var cells = new SortedDictionary<int, string>();
                foreach ((int column, (string text, int remaining)) in spans.ToArray())
                {
                    cells[column] = text;
                    if (remaining == 1) spans.Remove(column); else spans[column] = (text, remaining - 1);
                }
                int index = 0;
                foreach (IElement cell in tr.Children.Where(n => n.LocalName is "td" or "th"))
                {
                    while (cells.ContainsKey(index)) index++;
                    int width = Span(cell, "colspan");
                    int height = Span(cell, "rowspan");
                    for (int offset = 0; offset < width; offset++)
                    {
                        cells.Add(index + offset, Text(cell));
                        if (height > 1) spans.Add(index + offset, (Text(cell), height - 1));
                    }
                    index += width;
                }
                if (cells.Count > 0) rows.Add(Enumerable.Range(0, cells.Keys.Max() + 1).Select(i => cells.GetValueOrDefault(i, "")).ToArray());
            }
            if (rows.Count > 0) result.Add(new Table(headings.Values.ToArray(), rows));
        }
        return result;
    }

    private static int Span(IElement element, string name)
    {
        string? value = element.GetAttribute(name);
        if (value == null) return 1;
        if (!int.TryParse(value, out int count) || count < 1 || count > 1000)
            throw new InvalidOperationException($"Unsupported table span: {name}={value}");
        return count;
    }

    private static string Text(INode node)
    {
        var text = new StringBuilder();
        AppendText(node, text);
        return Regex.Replace(text.ToString(), @"\s+", " ").Trim();
    }

    private static void AppendText(INode node, StringBuilder text)
    {
        if (node is IText leaf) text.Append(leaf.Data);
        else
        {
            text.Append(' ');
            foreach (INode child in node.ChildNodes) AppendText(child, text);
            text.Append(' ');
        }
    }

    private static bool IsFieldTable(Table table) => table.Rows[0].Contains("Type") && table.Rows[0].Contains("Description");

    private static List<(string Section, string Code)> ReadExamples(IDocument html)
    {
        var result = new List<(string, string)>();
        string section = "";
        foreach (IElement element in html.All)
        {
            if (Regex.IsMatch(element.LocalName, "^h[1-6]$") && Text(element) is "Request" or "Response") section = Text(element);
            if (element.LocalName == "pre") result.Add((section, element.TextContent));
        }
        return result;
    }

    private static JsonObject? ReadContract(List<Table> tables, List<(string Section, string Code)> examples, string section, List<Table> typeEvidence)
    {
        Table? fields = tables.SingleOrDefault(t => IsFieldTable(t) &&
            (t.Section.LastOrDefault() == section || section == "Request" && t.Section.LastOrDefault() == "Body"));
        JsonObject? result = fields == null ? null : ParseFields(fields, tables);
        bool hasBodyExample = false;
        foreach (var example in examples.Where(e => e.Section == section))
        {
            Match objectStart = Regex.Match(example.Code, "\\{\\s*\"");
            int start = objectStart.Success ? objectStart.Index : -1;
            int end = example.Code.LastIndexOf('}');
            if (start < 0 || end < start) continue;
            hasBodyExample = true;
            string json = example.Code[start..(end + 1)];
            var placeholders = new HashSet<string>(StringComparer.Ordinal);
            json = Regex.Replace(json, @"(?<=:)\s*\{([A-Z][A-Z0-9_]*)\}", m =>
            {
                string marker = "__placeholder_" + m.Groups[1].Value;
                placeholders.Add(marker);
                return JsonSerializer.Serialize(marker);
            });
            JsonObject? value;
            try { value = JsonNode.Parse(json, documentOptions: new() { AllowTrailingCommas = true }) as JsonObject; }
            catch (JsonException) { continue; }
            if (value == null) continue;
            if (result == null)
            {
                result = InferExample(value, typeEvidence, placeholders);
                result["x-schema-source"] = "documentation-example";
            }
            ReconcileNames(result, value);
        }
        if (result == null && hasBodyExample)
            throw new InvalidOperationException($"Unable to parse documented {section} body.");
        if (result != null)
        {
            foreach (Table nested in tables.Where(IsFieldTable))
            {
                Match match = Regex.Match(nested.Section.LastOrDefault() ?? "", @"^Nested (\w+) struct$", RegexOptions.IgnoreCase);
                if (match.Success && result["properties"]?[match.Groups[1].Value] != null)
                    result["properties"]![match.Groups[1].Value] = ParseFields(nested, tables);
            }
        }
        return result;
    }

    private static JsonObject InferExample(JsonNode? value, List<Table> tables, HashSet<string> placeholders, string? field = null)
    {
        if (value is JsonObject obj)
        {
            var schema = ObjectSchema();
            foreach (var property in obj) AddProperty(schema, property.Key, InferExample(property.Value, tables, placeholders, property.Key));
            return schema;
        }
        if (value is JsonArray array)
            return new JsonObject { ["type"] = "array", ["items"] = array.Count == 0 ? new JsonObject() : InferExample(array[0], tables, placeholders) };
        if (value is JsonValue scalar && scalar.TryGetValue<string>(out string? text) && placeholders.Contains(text))
        {
            string[] types = tables.Where(IsFieldTable).SelectMany(t => t.Rows.Skip(1)
                .Where(r => r.Take(Array.IndexOf(t.Rows[0], "Type")).Contains(field))
                .Select(r => r[Array.IndexOf(t.Rows[0], "Type")])).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (types.Length != 1) throw new InvalidOperationException($"Cannot resolve documented placeholder type: {field}");
            JsonObject schema = TypeSchema(types[0], tables);
            schema["x-schema-source"] = "documented-field-type";
            return schema;
        }
        return value?.GetValueKind() switch
        {
            JsonValueKind.String => new JsonObject { ["type"] = "string" },
            JsonValueKind.True or JsonValueKind.False => new JsonObject { ["type"] = "boolean" },
            JsonValueKind.Number => new JsonObject { ["type"] = "number" },
            _ => new JsonObject { ["nullable"] = true }
        };
    }

    private static void ReconcileNames(JsonObject schema, JsonObject example)
    {
        if (schema["properties"] is not JsonObject properties) return;
        foreach (var property in properties.ToArray())
        {
            string[] matches = example.Select(p => p.Key).Where(k => k.Replace("_", "") == property.Key.Replace("_", "")).ToArray();
            if (matches.Length != 1) continue;
            string name = matches[0];
            if (name != property.Key)
            {
                properties.Remove(property.Key);
                properties.Add(name, property.Value);
            }
            if (property.Value is JsonObject nested && example[name] is JsonObject nestedExample) ReconcileNames(nested, nestedExample);
        }
    }

    internal static async Task FixDocument(IOpenApiFixer fixer, IFileUtil fileUtil, JsonObject document, string source, string destination, CancellationToken cancellationToken = default)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        await fileUtil.Write(source, document.ToJsonString(options), cancellationToken: cancellationToken);
        await fixer.Fix(source, destination, cancellationToken);
        JsonObject fixedDocument = JsonNode.Parse(await fileUtil.Read(destination, cancellationToken: cancellationToken))!.AsObject();
        var paths = new JsonObject();
        // The generic fixer strips trailing slashes; TikTok's documented wire paths must remain exact.
        foreach ((string path, JsonNode? original) in document["paths"]!.AsObject())
        {
            var operation = original!.AsObject().First(p => p.Value?["operationId"] != null);
            string operationId = operation.Value!["operationId"]!.GetValue<string>();
            JsonNode fixedPath = fixedDocument["paths"]!.AsObject().Single(p =>
                p.Value?[operation.Key]?["operationId"]?.GetValue<string>() == operationId).Value!;
            paths.Add(path, fixedPath.DeepClone());
        }
        if (paths.Count != fixedDocument["paths"]!.AsObject().Count)
            throw new InvalidOperationException("The fixer changed the endpoint inventory.");
        fixedDocument["paths"] = paths;
        await fileUtil.Write(destination, fixedDocument.ToJsonString(options), cancellationToken: cancellationToken);
    }
}
