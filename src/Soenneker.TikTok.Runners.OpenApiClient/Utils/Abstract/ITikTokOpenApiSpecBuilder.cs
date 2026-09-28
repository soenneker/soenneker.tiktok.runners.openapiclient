using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.TikTok.Runners.OpenApiClient.Utils.Abstract;

/// <summary>Builds the developer Content Posting OpenAPI document from TikTok's official documentation.</summary>
public interface ITikTokOpenApiSpecBuilder
{
    /// <summary>Discovers and downloads official posting references through the documentation navigation and converts their typed tables into OpenAPI 3.0.</summary>
    /// <remarks>Fails on missing tables, unsupported types, or unexpected endpoint changes. This is a derived specification, not a TikTok-published OpenAPI document.</remarks>
    ValueTask<JsonObject> Build(CancellationToken cancellationToken = default);

    /// <summary>Converts official article HTML, keyed by documentation slug, without making network requests.</summary>
    ValueTask<JsonObject> BuildFromDocuments(IReadOnlyDictionary<string, string> documents, CancellationToken cancellationToken = default);
}
