using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TcpQueueProxy.Options;

namespace TcpQueueProxy.Services
{
    /// <summary>
    /// Manages per-target queues. Each target has its own worker task.
    /// A hung target session must never block other targets.
    /// </summary>
    public class TcpQueueManager : ITcpQueueManager
    {
        private readonly ConcurrentDictionary<string, TargetWorker> _workers = new();
        private readonly ILoggerFactory _loggerFactory;
        private readonly TcpQueueOptions _options;

        public TcpQueueManager(
            ILoggerFactory loggerFactory,
            IOptions<TcpQueueOptions> options)
        {
            _loggerFactory = loggerFactory;
            _options = options.Value;
        }

        public void Enqueue(ListenerConfig listenerConfig, TcpClient client)
        {
            var key = listenerConfig.TargetKey;

            var worker = _workers.GetOrAdd(key, k =>
            {
                var logger = _loggerFactory.CreateLogger<TargetWorker>();
                return new TargetWorker(
                    listenerConfig,
                    _options.SessionTimeoutSeconds,
                    logger);
            });

            worker.Enqueue(client);
        }

        /// <summary>
        /// Worker for a single target (IP:Port).
        /// Processes sessions one-by-one in its own task.
        /// </summary>
        private sealed class TargetWorker
        {
            private readonly ListenerConfig _config;
            private readonly int _sessionTimeoutSeconds;
            private readonly ILogger _logger;

            private readonly Channel<TcpClient> _channel;
            private readonly CancellationTokenSource _workerCts;
            private readonly Task _workerTask;

            public TargetWorker(
                ListenerConfig config,
                int sessionTimeoutSeconds,
                ILogger logger)
            {
                _config = config;
                _sessionTimeoutSeconds = sessionTimeoutSeconds;
                _logger = logger;

                // Unbounded queue for this target only
                _channel = Channel.CreateUnbounded<TcpClient>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false
                });

                // Worker lifetime CTS (per target, not global)
                _workerCts = new CancellationTokenSource();

                // Start the processing loop
                _workerTask = Task.Run(ProcessQueueAsync);
            }

            public void Enqueue(TcpClient client)
            {
                // Fire-and-forget: unbounded, so TryWrite should always succeed
                if (!_channel.Writer.TryWrite(client))
                {
                    _logger.LogWarning(
                        "Failed to enqueue client for target {Target}. Closing client.",
                        _config.TargetKey);

                    client.Close();
                    client.Dispose();
                }
            }

            private async Task ProcessQueueAsync()
            {
                _logger.LogInformation(
                    "Target worker started for {Target} (Description: {Description})",
                    _config.TargetKey,
                    _config.Description);

                try
                {
                    // Each target has its own queue and worker loop.
                    // If one target hangs, only this worker is affected.
                    await foreach (var client in _channel.Reader.ReadAllAsync(_workerCts.Token))
                    {
                        try
                        {
                            await HandleSessionAsync(client, _workerCts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            // Worker is being shut down
                            client.Close();
                            client.Dispose();
                            break;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(
                                ex,
                                "Error while handling session for target {Target}",
                                _config.TargetKey);
                        }
                        finally
                        {
                            try
                            {
                                client.Close();
                                client.Dispose();
                            }
                            catch
                            {
                                // ignore
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // workerCts was cancelled -> normal shutdown
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Fatal error in worker loop for target {Target}",
                        _config.TargetKey);
                }

                _logger.LogInformation(
                    "Target worker stopped for {Target}",
                    _config.TargetKey);
            }

            private async Task HandleSessionAsync(TcpClient client, CancellationToken workerToken)
            {
                _logger.LogInformation(
                    "Starting session for target {Target} from {Remote}",
                    _config.TargetKey,
                    client.Client.RemoteEndPoint);

                // Per-session timeout to prevent hangs from blocking this worker forever
                using var sessionTimeoutCts = new CancellationTokenSource(
                    TimeSpan.FromSeconds(_sessionTimeoutSeconds));

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    workerToken,
                    sessionTimeoutCts.Token);

                var token = linkedCts.Token;

                using var targetClient = new TcpClient();

                // Connect with timeout
                await targetClient.ConnectAsync(_config.TargetHost, _config.TargetPort, token);

                using NetworkStream clientStream = client.GetStream();
                using NetworkStream targetStream = targetClient.GetStream();

                // Two pipes: client->target and target->client
                var upstreamTask = PipeAsync(clientStream, targetStream, token);
                var downstreamTask = PipeAsync(targetStream, clientStream, token);

                // If one side finishes or times out, we cancel the other
                var completed = await Task.WhenAny(upstreamTask, downstreamTask);

                if (completed == upstreamTask)
                {
                    _logger.LogDebug(
                        "Upstream (client->target) completed for {Target}",
                        _config.TargetKey);
                }
                else
                {
                    _logger.LogDebug(
                        "Downstream (target->client) completed for {Target}",
                        _config.TargetKey);
                }

                // Cancel the other direction
                linkedCts.Cancel();

                try
                {
                    await Task.WhenAll(upstreamTask, downstreamTask);
                }
                catch (OperationCanceledException)
                {
                    // Expected when we cancel
                }

                if (sessionTimeoutCts.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        "Session timeout for target {Target}. Session aborted.",
                        _config.TargetKey);
                }

                _logger.LogInformation(
                    "Session finished for target {Target} from {Remote}",
                    _config.TargetKey,
                    client.Client.RemoteEndPoint);
            }

            private static async Task PipeAsync(
                NetworkStream source,
                NetworkStream destination,
                CancellationToken cancellationToken)
            {
                var buffer = new byte[8192];

                while (!cancellationToken.IsCancellationRequested)
                {
#if NET9_0_OR_GREATER || NET10_0_OR_GREATER
                    var bytesRead = await source.ReadAsync(buffer, cancellationToken);
#else
                    var bytesRead = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
#endif
                    if (bytesRead == 0)
                    {
                        // Connection closed
                        break;
                    }

#if NET9_0_OR_GREATER || NET10_0_OR_GREATER
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
                // Stop only this target worker
                _workerCts.Cancel();
            }
        }
    }
}
