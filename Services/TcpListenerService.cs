using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TcpQueueProxy.Options;

namespace TcpQueueProxy.Services
{
    public class TcpListenerService : BackgroundService
    {
        private readonly ILogger<TcpListenerService> _logger;
        private readonly ITcpQueueManager _queueManager;
        private readonly TcpQueueOptions _options;
        private readonly List<TcpListener> _listeners = new();

        public TcpListenerService(
            ILogger<TcpListenerService> logger,
            ITcpQueueManager queueManager,
            IOptions<TcpQueueOptions> options)
        {
            _logger = logger;
            _queueManager = queueManager;
            _options = options.Value;
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Configured listeners: {Count}", _options.Listeners.Count);

            if (_options.Listeners.Count == 0)
            {
                _logger.LogWarning("No listeners configured. Service will start but do nothing.");
                return;
            }

            foreach (var listenerConfig in _options.Listeners)
            {
                var listener = new TcpListener(IPAddress.Any, listenerConfig.ListenPort);
                listener.Start();
                _listeners.Add(listener);

                _logger.LogInformation(
                    "Started listener on port {ListenPort} -> {TargetHost}:{TargetPort} ({Description})",
                    listenerConfig.ListenPort,
                    listenerConfig.TargetHost,
                    listenerConfig.TargetPort,
                    listenerConfig.Description ?? "no description");

                _ = Task.Run(
                    () => AcceptLoopAsync(listener, listenerConfig, cancellationToken),
                    cancellationToken);
            }

            await base.StartAsync(cancellationToken);
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("TcpListenerService is running.");
            return Task.CompletedTask;
        }

        private async Task AcceptLoopAsync(
            TcpListener listener,
            ListenerConfig listenerConfig,
            CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient? client = null;

                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken);

                    _logger.LogDebug(
                        "Accepted new client on port {Port} from {Remote}",
                        listenerConfig.ListenPort,
                        client.Client.RemoteEndPoint);

                    _queueManager.Enqueue(listenerConfig, client);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    _logger.LogInformation(
                        "Listener on port {Port} has been disposed.",
                        listenerConfig.ListenPort);
                    break;
                }
                catch (InvalidOperationException ex)
                {
                    _logger.LogInformation(
                        "Listener on port {Port} is no longer listening: {Message}",
                        listenerConfig.ListenPort,
                        ex.Message);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Error while accepting client on port {Port}",
                        listenerConfig.ListenPort);

                    client?.Close();
                    client?.Dispose();
                }
            }
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Stopping TcpListenerService...");

            foreach (var listener in _listeners)
            {
                try { listener.Stop(); } catch { /* ignore */ }
            }

            return base.StopAsync(cancellationToken);
        }
    }
}
