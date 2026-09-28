using Soenneker.Tests.HostedUnit;

namespace Soenneker.TikTok.Runners.OpenApiClient.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public sealed class TikTokOpenApiClientRunnerTests : HostedUnitTest
{
    public TikTokOpenApiClientRunnerTests(Host host) : base(host)
    {
    }

    [Test]
    public void Default()
    {

    }
}
