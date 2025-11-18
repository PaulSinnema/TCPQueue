using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TcpQueueProxy.Options;
using TcpQueueProxy.Services;

var builder = Host.CreateApplicationBuilder(args);

string configDirectory = Environment.GetEnvironmentVariable("CONFIG_PATH") ?? AppContext.BaseDirectory;

Console.WriteLine($"Configuration path: {configDirectory}");

if (!Directory.Exists(configDirectory))
{
    Console.WriteLine($"Config directory does not exist: {configDirectory}");
}

string appSettingsPath = Path.Combine(configDirectory, "appsettings.json");

Console.WriteLine($"Settings path: {appSettingsPath}");

// Configuration
if (File.Exists(appSettingsPath))
{
    builder.Configuration
    .AddJsonFile(appSettingsPath, optional: false, reloadOnChange: true)
    .AddEnvironmentVariables();
}
else
{
    Console.WriteLine("⚠️ Warning: appsettings.json missing!");
}

builder.Configuration.AddEnvironmentVariables();

builder.Services.Configure<TcpQueueOptions>(builder.Configuration.GetSection("TcpQueue"));
builder.Services.AddSingleton<ITcpQueueManager, TcpQueueManager>();
builder.Services.AddHostedService<TcpListenerService>();

// Logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

var host = builder.Build();
await host.RunAsync();
