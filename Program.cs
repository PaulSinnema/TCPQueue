using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TcpQueueProxy;

/// <summary>
/// Entry point of the TCP Queue Proxy application.
/// Configures hosting, dependency injection, configuration sources and logging.
/// </summary>
public class Program
{
    /// <summary>
    /// Main entry point. Starts the .NET Generic Host.
    /// </summary>
    public static async Task Main(string[] args) =>
        await CreateHostBuilder(args).Build().RunAsync();

    /// <summary>
    /// Creates and configures the host builder with configuration, services and logging.
    /// </summary>
    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((_, config) =>
            {
                config.SetBasePath(Directory.GetCurrentDirectory())
                      .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                      .AddJsonFile("/app/config/appsettings.json", optional: true, reloadOnChange: true) // Docker volume
                      .AddEnvironmentVariables();
            })
            .ConfigureServices((context, services) =>
            {
                services.Configure<ProxyConfig>(context.Configuration.GetSection("TcpQueue"));
                services.AddSingleton<QueueManager>();
                services.AddHostedService<TcpProxyService>();
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddSimpleConsole(options =>
                {
                    options.IncludeScopes = true;
                    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
                });
                logging.SetMinimumLevel(LogLevel.Information);
            });
}