using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TcpQueueProxy.Options;

namespace TcpQueueProxy.Services
{
    /// <summary>
    /// Manages per-target queues. Each target gets its own worker that
    /// processes sessions strictly sequentially.
    /// </summary>
    public class TcpQueueManager : ITcpQueueManager
    {
        private readonly ConcurrentDictionary<string, TargetWorker> _workers = new();
        private readonly ILogger<TcpQueueManager> _logger;
        private readonly TcpQueueOptions _options;

        public TcpQueueManager(
            ILogger<TcpQueueManager> logger,
            IOptions<TcpQueueOptions> options)
        {
            _logger = logger;
            _options = options.Value;
        }

        public void Enqueue(ListenerConfig listenerConfig, TcpClient client)
        {
            var key = listenerConfig.TargetKey;

            var worker = _workers.GetOrAdd(key, k =>
            {
                _logger.LogInformation("Creating queue worker for target {Target}", k);
                return new TargetWorker(listenerConfig, _options.SessionTimeoutSeconds, _logger);
            });

            worker.Enqueue(client);
        }

        /// <summary>
        /// Represents a worker for a single target (IP:Port).
        /// It processes client sessions one-by-one using a Channel.
        /// </summary>
        private sealed class TargetWorker
        {
            private readonly Channel<TcpClient> _channel;
            private readonly ListenerConfig _config;
            private readonly int _sessionTimeoutSeconds;
            private readonly ILogger _logger;
            private readonly Task _processingTask;
            private readonly CancellationTokenSource _cts = new();

            public TargetWorker(ListenerConfig config, int sessionTimeoutSeconds, ILogger logger)
            {
                _config = config;
                _sessionTimeoutSeconds = sessionTimeoutSeconds;
                _logger = logger;

                _channel = Channel.CreateUnbounded<TcpClient>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false
                });

                _processingTask = Task.Run(ProcessQueueAsync);
            }

            public void Enqueue(TcpClient client)
            {
                // Fire-and-forget, Channel is unbounded
                _channel.Writer.TryWrite(client);
            }

            private async Task ProcessQueueAsync()
            {
                _logger.LogInformation(
                    "Target worker for {Target} started",
                    _config.TargetKey);

                try
                {
                    await foreach (var client in _channel.Reader.ReadAllAsync(_cts.Token))
                    {
                        try
                        {
                            await HandleSessionAsync(client);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex,
                                "Error while handling session for target {Target}",
                                _config.TargetKey);
                        }
                        finally
                        {
                            client.Close();
                            client.Dispose();
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected during shutdown
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Fatal error in worker for target {Target}",
                        _config.TargetKey);
                }

                _logger.LogInformation(
                    "Target worker for {Target} stopped",
                    _config.TargetKey);
            }

            private async Task HandleSessionAsync(TcpClient client)
            {
                // Only one session at a time per target; this method is called sequentially.
                _logger.LogInformation(
                    "Handling new session from {Remote} to target {Target}",
                    client.Client.RemoteEndPoint,
                    _config.TargetKey);

                using var timeoutCts = new CancellationTokenSource(
                    TimeSpan.FromSeconds(_sessionTimeoutSeconds));

                using var linkedCts =
                    CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, timeoutCts.Token);

                using var targetClient = new TcpClient();

                await targetClient.ConnectAsync(_config.TargetHost, _config.TargetPort, linkedCts.Token);

                using NetworkStream clientStream = client.GetStream();
                using NetworkStream targetStream = targetClient.GetStream();

                // Bi-directional piping: one upstream (client -> target) and one
                // downstream (target -> client). As soon as one direction ends,
                // we cancel the other.
                var t1 = PipeAsync(clientStream, targetStream, linkedCts.Token);
                var t2 = PipeAsync(targetStream, clientStream, linkedCts.Token);

                var completed = await Task.WhenAny(t1, t2);
                if (completed == t1)
                {
                    _logger.LogDebug("Client->Target pipe completed for {Target}", _config.TargetKey);
                }
                else
                {
                    _logger.LogDebug("Target->Client pipe completed for {Target}", _config.TargetKey);
                }

                // Cancel the other direction and wait for cleanup
                linkedCts.Cancel();
                try
                {
                    await Task.WhenAll(t1, t2);
                }
                catch (OperationCanceledException)
                {
                    // Expected when we cancel
                }

                _logger.LogInformation(
                    "Session from {Remote} to {Target} finished",
                    client.Client.RemoteEndPoint,
                    _config.TargetKey);
            }

            private static async Task PipeAsync(
                NetworkStream source,
                NetworkStream destination,
                CancellationToken cancellationToken)
            {
                var buffer = new byte[8192];

                while (!cancellationToken.IsCancellationRequested)
                {
                    int bytesRead;
#if NET9_0_OR_GREATER
                    bytesRead = await source.ReadAsync(buffer, cancellationToken);
#else
                    bytesRead = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
#endif
                    if (bytesRead == 0)
                    {
                        // Connection closed
                        break;
                    }

#if NET9_0_OR_GREATER
                    await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    await destination.FlushAsync(cancellationToken);
#else
                    await destination.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                    await destination.FlushAsync(cancellationToken);
#endif
                }
            }

            public void Stop()
            {
                _cts.Cancel();
            }
        }
    }
}
