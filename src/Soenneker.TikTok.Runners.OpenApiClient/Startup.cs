using Soenneker.Playwrights.Crawler.Registrars;
using Soenneker.Utils.File.Registrars;
using Soenneker.Utils.Directory.Registrars;
using Microsoft.Extensions.DependencyInjection;
using Soenneker.AngleSharp.Parser.Registrars;
using Soenneker.Kiota.Util.Registrars;
using Soenneker.Managers.Runners.Registrars;
using Soenneker.OpenApi.Fixer.Registrars;
using Soenneker.TikTok.Runners.OpenApiClient.Utils;
using Soenneker.TikTok.Runners.OpenApiClient.Utils.Abstract;

namespace Soenneker.TikTok.Runners.OpenApiClient;

/// <summary>
/// Console type startup
/// </summary>
public static class Startup
{
    // This method gets called by the runtime. Use this method to add services to the container.
    public static void ConfigureServices(IServiceCollection services)
    {
        services.SetupIoC();
    }

    public static IServiceCollection SetupIoC(this IServiceCollection services)
    {
        services.AddHostedService<ConsoleHostedService>()
                .AddSingleton<IFileOperationsUtil, FileOperationsUtil>()
                .AddRunnersManagerAsSingleton()
                .AddSingleton<ITikTokOpenApiSpecBuilder, TikTokOpenApiSpecBuilder>()
                .AddAngleSharpParserAsSingleton()
                .AddPlaywrightCrawlerAsSingleton()
                .AddFileUtilAsSingleton()
                .AddDirectoryUtilAsSingleton()
                .AddOpenApiFixerAsSingleton()
                .AddKiotaUtilAsSingleton();

        return services;
    }
}
