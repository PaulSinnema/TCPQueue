using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace TcpQueueProxy;

/// <summary>
/// A high-performance TCP proxy that forwards raw TCP traffic while guaranteeing 
/// that only one active connection exists at any time per target endpoint (sequencing).
/// Perfect for devices/protocols that do not support concurrent connections (e.g. many Modbus devices, 
/// Home Assistant HTTP API, cheap IoT devices, etc.).
/// </summary>
public sealed class TcpProxyService : BackgroundService
{
    private readonly ProxyConfig _config;
    private readonly ILogger<TcpProxyService> _logger;

    /// <summary>
    /// One semaphore per unique target string. Ensures only one active connection per target.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _targetLocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="TcpProxyService"/> class.
    /// </summary>
    /// <param name="config">Proxy configuration containing forwarding rules.</param>
    /// <param name="logger">Logger instance.</param>
    public TcpProxyService(IOptions<ProxyConfig> config, ILogger<TcpProxyService> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    /// <summary>
    /// Starts all configured TCP listeners when the application host starts.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token that signals application shutdown.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var rule in _config.Forwards)
        {
            var listener = new TcpListener(IPAddress.Any, rule.ListenPort);
            listener.Start();

            _logger.LogInformation("Listening on 0.0.0.0:{ListenPort} → {Target} (strict sequencing enabled)",
                rule.ListenPort, rule.Target);

            // Fire-and-forget accept loop – runs independently per listen port
            _ = AcceptLoopAsync(listener, rule.Target, stoppingToken);
        }

        // Keep the service alive until shutdown
        await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Continuously accepts incoming clients on the specified listener and forwards them with sequencing.
    /// </summary>
    /// <param name="listener">The TCP listener.</param>
    /// <param name="target">Target in "host:port" format.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task AcceptLoopAsync(TcpListener listener, string target, CancellationToken cancellationToken)
    {
        var (host, port) = ParseTarget(target);

        // One semaphore per target → guarantees only one active connection at a time
        var semaphore = _targetLocks.GetOrAdd(target, _ => new SemaphoreSlim(1, 1));

        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);

                // Wait until this target is free → enforces sequencing
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

                // Handle the client in a fire-and-forget task (lock is released in finally block)
                _ = HandleClientWithSequencingAsync(client, host, port, semaphore, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                var localPort = listener.LocalEndpoint is IPEndPoint ipep ? ipep.Port : -1;
                _logger.LogError(ex, $"Failed to accept client on port {localPort}");
                client?.Close();
            }
        }
    }

    /// <summary>
    /// Handles a single client connection: connects to the target and performs bidirectional forwarding.
    /// The semaphore guarantees exclusive access to the target.
    /// </summary>
    /// <param name="client">Accepted client from the listener.</param>
    /// <param name="targetHost">Remote host to connect to.</param>
    /// <param name="targetPort">Remote port to connect to.</param>
    /// <param name="semaphore">Semaphore that protects the target.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task HandleClientWithSequencingAsync(
        TcpClient client,
        string targetHost,
        int targetPort,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        try
        {
            using var upstream = new TcpClient();
            await upstream.ConnectAsync(targetHost, targetPort, cancellationToken).ConfigureAwait(false);

            await using var upstreamStream = upstream.GetStream();
            await using var clientStream = client.GetStream();

            _logger.LogInformation("Sequenced connection established: {ClientEndpoint} → {TargetEndpoint}",
                client.Client.LocalEndPoint,
                upstream.Client.RemoteEndPoint);

            // Bidirectional copy – raw TCP forwarding
            var clientToTarget = clientStream.CopyToAsync(upstreamStream, 81920, cancellationToken);
            var targetToClient = upstreamStream.CopyToAsync(clientStream, 81920, cancellationToken);

            // Wait for either direction to close
            await Task.WhenAny(clientToTarget, targetToClient).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Connection closed (client or target disconnected)");
        }
        finally
        {
            // Always release the lock – even on error or cancellation
            semaphore.Release();
            try { client.Close(); }
            catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Parses a target string in "host:port" format.
    /// </summary>
    /// <param name="target">The target string.</param>
    /// <returns>A tuple containing host and port.</returns>
    /// <exception cref="FormatException">Thrown when the format is invalid.</exception>
    private static (string host, int port) ParseTarget(string target)
    {
        var parts = target.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var port))
            throw new FormatException($"Invalid target format: '{target}'. Expected 'host:port'.");

        return (parts[0], port);
    }
}