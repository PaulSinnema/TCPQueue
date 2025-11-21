using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TcpQueueProxy.Extensions;

namespace TcpQueueProxy;

public class Program
{
    public static async Task Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (sender, eventArgs) =>
        {
            var senderType = sender?.GetType() ?? null;
            var ex = (Exception)eventArgs.ExceptionObject;

            Console.WriteLine($"🚨 Critical unhandled exception occurred: {ex.ToDetailedString()}");
            Console.WriteLine($"Sender is: {senderType?.FullName} IsTerminating: {eventArgs.IsTerminating}");
        };

        await CreateHostBuilder(args).Build().RunAsync();
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((hostingContext, config) =>
            {
                Console.WriteLine("TcpQueue 1.0 is starting ...");

                config.Sources.Clear(); // voorkom ingebakken config

                // 1. Container mode: CONFIG_PATH environment variable gezet?
                var configPath = Environment.GetEnvironmentVariable("CONFIG_PATH");

                if (!string.IsNullOrWhiteSpace(configPath))
                {
                    // CONTAINER: gebruik alleen externe map
                    Console.WriteLine($"[TcpQueueProxy] CONTAINER MODE → using CONFIG_PATH: {configPath}");

                    if (Directory.Exists(configPath))
                    {
                        Console.WriteLine($"Directory exists: {configPath}");

                        var files = Directory.GetFiles(configPath);

                        Console.WriteLine($"Files in directory: {configPath} count: {files.Length}");

                        foreach (var file in files)
                        {
                            Console.WriteLine(file);
                        }

                        var mainFile = Path.Combine(configPath, "appsettings.json");
                        var envFile = Path.Combine(configPath, $"appsettings.{hostingContext.HostingEnvironment.EnvironmentName}.json");

                        Console.WriteLine($"Main file is: {mainFile}");
                        Console.WriteLine($"Environment file is: {envFile}");

                        if (File.Exists(mainFile))
                        {
                            config.AddJsonFile(mainFile, optional: false, reloadOnChange: true);
                            Console.WriteLine($"[TcpQueueProxy] Loaded: {mainFile}");
                        }
                        else
                        {
                            Console.WriteLine($"[TcpQueueProxy] FATAL: {mainFile} not found!");
                            Console.WriteLine($"[TcpQueueProxy] Mount your config to: /volume1/containers/tcpqueue/config");
                        }

                        if (File.Exists(envFile))
                            config.AddJsonFile(envFile, optional: true, reloadOnChange: true);
                    }
                    else
                    {
                        Console.WriteLine($"Directory does not exist {configPath}");
                    }
                }
                else
                {
                    // 2. DEVELOPMENT MODE: geen CONFIG_PATH → gebruik lokale bestanden
                    Console.WriteLine("[TcpQueueProxy] DEVELOPMENT MODE → using local appsettings.json");

                    var basePath = Directory.GetCurrentDirectory();

                    config
                        .SetBasePath(basePath)
                        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                        .AddJsonFile($"appsettings.{hostingContext.HostingEnvironment.EnvironmentName}.json", optional: true, reloadOnChange: true);
                }

                // Environment variables altijd laatste
                config.AddEnvironmentVariables();
            })
            .ConfigureServices((hostContext, services) =>
            {
                var proxySection = hostContext.Configuration.GetSection("TcpQueue");

                if (!proxySection.Exists() || proxySection.GetChildren().Any() == false)
                {
                    Console.WriteLine("[TcpQueueProxy] No forwarding rules found in configuration → nothing to do.");
                    return;
                }

                services.Configure<ProxyConfig>(proxySection);
                services.AddHostedService<TcpProxyService>();
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddSimpleConsole(options =>
                {
                    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
                    options.SingleLine = true;
                });
                logging.SetMinimumLevel(LogLevel.Information);
            });
}