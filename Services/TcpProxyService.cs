// TcpProxyService.cs – finale, productie-ready versie met sequencing per target
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace TcpQueueProxy;

public class TcpProxyService : BackgroundService
{
    private readonly ProxyConfig _config;
    private readonly ILogger<TcpProxyService> _logger;

    // Eén semaphore per target → zorgt dat er maar 1 verbinding tegelijk actief is
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _targetLocks = new();

    public TcpProxyService(IOptions<ProxyConfig> config, ILogger<TcpProxyService> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var rule in _config.Forwards)
        {
            var listener = new TcpListener(IPAddress.Any, rule.ListenPort);
            listener.Start();

            _logger.LogInformation("Listening on 0.0.0.0:{Port} → {Target} (sequenced)",
                rule.ListenPort, rule.Target);

            _ = AcceptLoopAsync(listener, rule.Target, stoppingToken);
        }

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task AcceptLoopAsync(TcpListener listener, string target, CancellationToken ct)
    {
        var (host, port) = ParseTarget(target);

        // Zorg dat er altijd een lock bestaat voor dit target
        var semaphore = _targetLocks.GetOrAdd(target, _ => new SemaphoreSlim(1, 1));

        while (!ct.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);

                // WACHT hier tot het target vrij is → sequencing gegarandeerd!
                await semaphore.WaitAsync(ct);

                // Fire-and-forget de handler (lock blijft vast tot verbinding dicht is
                _ = HandleClientWithSequencing(client, host, port, semaphore, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accepting client on port {Port}", listener.LocalEndpoint);
                client?.Close();
            }
        }
    }

    private async Task HandleClientWithSequencing(
        TcpClient client,
        string targetHost,
        int targetPort,
        SemaphoreSlim semaphore,
        CancellationToken ct)
    {
        try
        {
            using var upstream = new TcpClient();
            await upstream.ConnectAsync(targetHost, targetPort, ct);

            await using var upstreamStream = upstream.GetStream();
            await using var clientStream = client.GetStream();

            _logger.LogInformation("Sequenced connection: {Local} → {Remote}",
                client.Client.LocalEndPoint, upstream.Client.RemoteEndPoint);

            var t1 = clientStream.CopyToAsync(upstreamStream, 81920, ct);
            var t2 = upstreamStream.CopyToAsync(clientStream, 81920, ct);

            await Task.WhenAny(t1, t2);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Connection closed (normal or error)");
        }
        finally
        {
            // BELANGRIJK: altijd de lock vrijgeven, ook bij fout
            semaphore.Release();
            client.Close();
        }
    }

    private (string host, int port) ParseTarget(string target)
    {
        var parts = target.Split(':');
        return (parts[0], int.Parse(parts[1]));
    }
}