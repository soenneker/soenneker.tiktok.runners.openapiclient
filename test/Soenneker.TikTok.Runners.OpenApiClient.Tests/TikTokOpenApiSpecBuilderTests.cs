using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Soenneker.AngleSharp.Parser.Registrars;
using Soenneker.Playwrights.Crawler.Registrars;
using Soenneker.Utils.File.Abstract;
using Soenneker.Utils.Directory.Abstract;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Soenneker.TikTok.Runners.OpenApiClient.Utils;
using System.Threading;

namespace Soenneker.TikTok.Runners.OpenApiClient.Tests;

public sealed class TikTokOpenApiSpecBuilderTests
{
    private static async Task<Dictionary<string, string>> Documents(ServiceProvider provider, CancellationToken cancellationToken = default)
    {
        var documents = new Dictionary<string, string>();
        foreach (string path in await provider.GetRequiredService<IDirectoryUtil>().GetFilesByExtension(Path.Combine(AppContext.BaseDirectory, "Fixtures"), ".json", false, cancellationToken: cancellationToken))
        {
            JsonNode doc = JsonNode.Parse(await provider.GetRequiredService<IFileUtil>().Read(path, cancellationToken: cancellationToken))!;
            documents.Add(doc["slug"]!.GetValue<string>(), doc["content"]!.GetValue<string>());
        }
        return documents;
    }

    private static ServiceProvider Services() => new ServiceCollection()
        .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
        .AddLogging()
        .AddAngleSharpParserAsSingleton()
        .AddPlaywrightCrawlerAsSingleton()
        .AddSingleton<TikTokOpenApiSpecBuilder>()
        .BuildServiceProvider();

    [Test]
    public async ValueTask OfficialTablesPreserveContracts(CancellationToken cancellationToken)
    {
        await using ServiceProvider provider = Services();
        JsonObject document = await provider.GetRequiredService<TikTokOpenApiSpecBuilder>().BuildFromDocuments(await Documents(provider, cancellationToken: cancellationToken), cancellationToken: cancellationToken);
        await Assert.That(document["paths"]!.AsObject().Count).IsEqualTo(5);
        await Assert.That(document["servers"]![0]!["url"]!.GetValue<string>()).IsEqualTo("https://open.tiktokapis.com");
        JsonNode schemas = document["components"]!["schemas"]!;
        JsonNode source = schemas["PostV2PostPublishVideoInitRequest"]!["properties"]!["source_info"]!;
        await Assert.That(source["properties"]!["video_size"]!["format"]!.GetValue<string>()).IsEqualTo("int64");
        await Assert.That(source["properties"]!["video_size"]!["x-tiktok-required"]!.GetValue<string>().Contains("FILE_UPLOAD")).IsTrue();
        JsonNode response = schemas["PostV2PostPublishVideoInitResponse"]!["properties"]!;
        await Assert.That(response["data"]!["properties"]!["upload_url"]!["type"]!.GetValue<string>()).IsEqualTo("string");
        await Assert.That(response["error"]!["properties"]!["log_id"] is not null).IsTrue();
        await Assert.That(response["error"]!["properties"]!["logid"] is null).IsTrue();
        JsonNode status = schemas["PostV2PostPublishStatusFetchResponse"]!["properties"]!["data"]!["properties"]!;
        await Assert.That(status["publicaly_available_post_id"]!["items"]!["format"]!.GetValue<string>()).IsEqualTo("int64");
        await Assert.That(schemas["PostV2PostPublishStatusFetchRequest"]!["properties"]!["publish_id"]!["type"]!.GetValue<string>()).IsEqualTo("string");
        await Assert.That(schemas["PostV2PostPublishContentInitRequest"]!["properties"]!["post_info"]!["properties"]!["privacy_level"] is not null).IsTrue();
        await Assert.That(document["paths"]!["/v2/post/publish/video/init/"]!["post"]!["responses"]!["403"]!.ToJsonString().Contains("spam_risk_user_banned_from_posting")).IsTrue();
        await Assert.That(document["paths"]!["/v2/post/publish/creator_info/query/"]!["post"]!["requestBody"] is null).IsTrue();
    }

    [Test]
    public async ValueTask UnlistedEndpointAndNamedObjectAreDerivedFromContent(CancellationToken cancellationToken)
    {
        await using ServiceProvider provider = Services();
        var docs = new Dictionary<string, string>
        {
            ["new-reference"] = """
                <table><tr><td>HTTP URL</td><td>/v3/widgets/</td></tr><tr><td>HTTP Method</td><td>PATCH</td></tr></table>
                <h2>Request</h2><h3>Body</h3>
                <table><tr><th>Field</th><th>Type</th><th>Description</th><th>Required</th></tr>
                <tr><td>settings</td><td>Widget Settings</td><td>Settings to update.</td><td>true</td></tr></table>
                <h3>Widget Settings</h3>
                <table><tr><th>Field</th><th>Type</th><th>Description</th></tr>
                <tr><td>enabled</td><td>bool</td><td>Whether enabled.</td></tr></table>
                <h2>Response</h2>
                <table><tr><th>Field</th><th>Type</th><th>Description</th></tr>
                <tr><td>revision</td><td>int64</td><td>New revision.</td></tr></table>
                """
        };
        JsonObject document = await provider.GetRequiredService<TikTokOpenApiSpecBuilder>().BuildFromDocuments(docs, cancellationToken: cancellationToken);
        await Assert.That(document["paths"]!["/v3/widgets/"]!["patch"]!["operationId"]!.GetValue<string>()).IsEqualTo("PatchV3Widgets");
        await Assert.That(document["components"]!["schemas"]!["PatchV3WidgetsRequest"]!["properties"]!["settings"]!["properties"]!["enabled"]!["type"]!.GetValue<string>()).IsEqualTo("boolean");
    }

    [Test]
    public async ValueTask UnparseableBodyFailsInsteadOfOmittingRequest(CancellationToken cancellationToken)
    {
        await using ServiceProvider provider = Services();
        var docs = new Dictionary<string, string>
        {
            ["broken-reference"] = """
                <table><tr><td>HTTP URL</td><td>/v3/widgets/</td></tr><tr><td>HTTP Method</td><td>POST</td></tr></table>
                <h2>Request</h2><pre>{"value": ???}</pre>
                <h2>Response</h2><pre>{"accepted": true}</pre>
                """
        };
        try { await provider.GetRequiredService<TikTokOpenApiSpecBuilder>().BuildFromDocuments(docs, cancellationToken: cancellationToken); }
        catch (InvalidOperationException e) when (e.Message.Contains("Unable to parse documented Request body", StringComparison.Ordinal)) { return; }
        throw new Exception("An unparseable body must fail generation.");
    }

    [Test]
    public async ValueTask UnsupportedTypeFailsInsteadOfGuessing(CancellationToken cancellationToken)
    {
        await using ServiceProvider provider = Services();
        var docs = await Documents(provider, cancellationToken: cancellationToken);
        const string slug = "content-posting-api-reference-direct-post";
        docs[slug] = docs[slug].Replace("int64", "unrecognized_type", StringComparison.Ordinal);
        try { await provider.GetRequiredService<TikTokOpenApiSpecBuilder>().BuildFromDocuments(docs, cancellationToken: cancellationToken); }
        catch (InvalidOperationException e) when (e.Message.Contains("Unsupported documented type", StringComparison.Ordinal)) { return; }
        throw new Exception("An unknown field type must fail generation.");
    }
}
