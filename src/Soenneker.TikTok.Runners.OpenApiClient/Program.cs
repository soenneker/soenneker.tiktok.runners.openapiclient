using Soenneker.Playwrights.Crawler.Registrars;
using Soenneker.Utils.File.Registrars;
using Soenneker.Utils.Directory.Registrars;
using Soenneker.AngleSharp.Parser.Registrars;
using Soenneker.Utils.File.Abstract;
using Soenneker.Utils.Directory.Abstract;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;
using Soenneker.Enums.DeployEnvironment;
using Soenneker.Extensions.LoggerConfiguration;
using Microsoft.Extensions.DependencyInjection;
using Soenneker.OpenApi.Fixer.Abstract;
using Soenneker.OpenApi.Fixer.Registrars;
using Soenneker.TikTok.Runners.OpenApiClient.Utils;

namespace Soenneker.TikTok.Runners.OpenApiClient;

public sealed class Program
{
    private static string? _environment;

    private static CancellationTokenSource? _cts;

    public static async Task Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--spec-output")
        {
            if (args.Length != 2) throw new ArgumentException("Usage: --spec-output <openapi.json>");
            string output = Path.GetFullPath(args[1]);
            await using ServiceProvider provider = new ServiceCollection().AddSingleton<IConfiguration>(new ConfigurationBuilder().AddEnvironmentVariables().Build()).AddLogging().AddOpenApiFixerAsSingleton()
                .AddAngleSharpParserAsSingleton()
                .AddPlaywrightCrawlerAsSingleton()
                .AddFileUtilAsSingleton()
                .AddDirectoryUtilAsSingleton().AddSingleton<TikTokOpenApiSpecBuilder>().BuildServiceProvider();
            IFileUtil fileUtil = provider.GetRequiredService<IFileUtil>();
            await provider.GetRequiredService<IDirectoryUtil>().Create(Path.GetDirectoryName(output)!);
            var document = await provider.GetRequiredService<TikTokOpenApiSpecBuilder>().Build();
            string temporary = Path.Combine(Path.GetDirectoryName(output)!, $".tiktok-{Guid.NewGuid():N}.json");
            try
            {
                await TikTokOpenApiSpecBuilder.FixDocument(provider.GetRequiredService<IOpenApiFixer>(), fileUtil, document, temporary, output);
                Console.WriteLine($"Generated TikTok posting specification: {output}");
            }
            finally
            {
                await fileUtil.DeleteIfExists(temporary);
            }
            return;
        }

        _environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        if (string.IsNullOrWhiteSpace(_environment))
            throw new Exception("ASPNETCORE_ENVIRONMENT is not set");

        // Declare CancellationTokenSource in a broader scope
        _cts = new CancellationTokenSource(); // Use 'using' to ensure proper disposal
        Console.CancelKeyPress += OnCancelKeyPress;

        try
        {
            await CreateHostBuilder(args).RunConsoleAsync(_cts.Token);
        }
        catch (Exception e)
        {
            Log.Error(e, "Stopped program because of exception");
            throw;
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress; // Detach the handler

            _cts.Dispose();
            await Log.CloseAndFlushAsync();
        }
    }

    /// <summary>
    /// Used for WebApplicationFactory, cannot delete, cannot change access, cannot change number of parameters.
    /// </summary>
    public static IHostBuilder CreateHostBuilder(string[] args)
    {
        DeployEnvironment envEnum = DeployEnvironment.FromName(_environment ?? throw new InvalidOperationException("ASPNETCORE_ENVIRONMENT is not set"));

        LoggerConfigurationExtension.BuildBootstrapLoggerAndSetGloballySync(envEnum);

        IHostBuilder? host = Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((hostingContext, builder) =>
            {
                builder.AddEnvironmentVariables();
                builder.SetBasePath(hostingContext.HostingEnvironment.ContentRootPath);
            })
            .UseSerilog()
            .ConfigureServices((_, services) => { Startup.ConfigureServices(services); });

        return host;
    }

    private static void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs eventArgs)
    {
        eventArgs.Cancel = true; // Prevents immediate termination
        _cts?.Cancel();
    }
}
