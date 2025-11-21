using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace TcpQueueProxy;

public sealed class TcpProxyService : BackgroundService
{
    private readonly ProxyConfig _config;
    private readonly ILogger<TcpProxyService> _logger;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _targetLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TcpListener> _listeners = new();

    public TcpProxyService(IOptions<ProxyConfig> config, ILogger<TcpProxyService> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TcpProxyService starting...");
        List<Task> tasks = new List<Task>();

        // Start listeners
        foreach (var rule in _config.Forwards)
        {
            var listener = new TcpListener(IPAddress.Any, rule.ListenPort);
            listener.Start();
            _listeners.Add(listener);

            _logger.LogInformation(
                "Listening on 0.0.0.0:{ListenPort} → {Target} (strict sequencing enabled)",
                rule.ListenPort, rule.Target);

            // Start accept loop per listener, maar met eigen taak
            var task = AcceptLoopAsync(listener, rule.Target, stoppingToken);

            tasks.Add(task);
        }

        await Task.WhenAll(tasks);

        _logger.LogInformation("TcpProxyService stopping, closing listeners...");

        foreach (var listener in _listeners)
        {
            try { listener.Stop(); } catch { /* ignore */ }
        }

        _logger.LogInformation("TcpProxyService stopped.");
    }

    private async Task AcceptLoopAsync(TcpListener listener, string target, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? client = null;

            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken);

                _logger.LogDebug("Accepted client {Client} for target {Target}",
                        client.Client.RemoteEndPoint, target);

                await HandleClientWithSequencingAsync(client, target, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Do nothing
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error in AcceptLoop for target {target}");

            }
            finally
            {
                client?.Close();
                client?.Dispose();
            }
        }

        _logger.LogInformation("AcceptLoop for {Target} stopped.", target);
    }

    private async Task HandleClientWithSequencingAsync(
        TcpClient client,
        string target,
        CancellationToken serviceToken)
    {
        var semaphore = _targetLocks.GetOrAdd(target, _ => new SemaphoreSlim(1, 1));

        await semaphore.WaitAsync(serviceToken);

        try
        {
            client.NoDelay = true;

            var targetParts = target.Split(':');
            var targetHost = targetParts[0];
            var targetPort = int.Parse(targetParts[1]);

            using var upstream = new TcpClient();

            // One overall timeout for this proxied exchange
            using var opCts = CancellationTokenSource.CreateLinkedTokenSource(serviceToken);
            opCts.CancelAfter(TimeSpan.FromSeconds(5)); // tune if needed

            _logger.LogInformation("New client {Client} → {Target}: connecting upstream...",
                client.Client.RemoteEndPoint, target);

            await upstream.ConnectAsync(targetHost, targetPort, opCts.Token);
            upstream.NoDelay = true;

            try
            {
                await using var clientStream = client.GetStream();
                await using var upstreamStream = upstream.GetStream();

                _logger.LogInformation("Streams ready for {Target}", target);

                await CopyWithLoggingAsync(clientStream, upstreamStream, target, "client→upstream", opCts.Token);
                await CopyWithLoggingAsync(upstreamStream, clientStream, target, "upstream→client", opCts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Proxy operation for {Target} timed out", target);
            }
            catch (Exception ex) when (!serviceToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Proxy error {Client} → {Target}",
                    client.Client.RemoteEndPoint, target);
            }
            finally
            {
                try { client.Close(); } catch { /* ignore */ }
                try { upstream.Close(); } catch { /* ignore */ }

                _logger.LogInformation("Closed client and upstream for {Target}", target);
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    // Helper, so you also log per direction & see if er bytes lopen
    private async Task CopyWithLoggingAsync(
        NetworkStream source,
        NetworkStream destination,
        string target,
        string direction,
        CancellationToken ct)
    {
        var bufferSize = 81920;
        var totalBytes = 0L;

        try
        {
            var buffer = new byte[bufferSize];

            while (!source.DataAvailable)
            {
                _logger.LogDebug($"No data available yet for {target}");
            }

            while (!ct.IsCancellationRequested && source.DataAvailable)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);

                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                await destination.FlushAsync(ct);

                await Task.Delay(50);

                totalBytes += read;
            }

            _logger.LogDebug("Copy {Direction} for {Target} finished, total bytes: {Bytes}",
                direction, target, totalBytes);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Copy {Direction} for {Target} cancelled/timeout after {Bytes} bytes",
                direction, target, totalBytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Copy {Direction} for {Target} failed after {Bytes} bytes",
                direction, target, totalBytes);
            throw;
        }
    }
}
