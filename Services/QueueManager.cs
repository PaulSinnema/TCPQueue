using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace TcpQueueProxy;

/// <summary>
/// Manages one independent, serialized processing queue per unique target endpoint.
/// Ensures that only one message is in flight at a time per target, while never blocking other targets.
/// Fully thread-safe and non-blocking.
/// </summary>
public class QueueManager
{
    private readonly ConcurrentDictionary<string, Channel<QueueItem>> _channels = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<QueueManager> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueueManager"/> class.
    /// </summary>
    public QueueManager(ILogger<QueueManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets or creates a dedicated channel (and background worker) for the specified target.
    /// </summary>
    /// <param name="target">Target endpoint in "host:port" format.</param>
    /// <returns>The channel used to queue requests for this target.</returns>
    public Channel<QueueItem> GetOrCreateChannel(string target)
    {
        return _channels.GetOrAdd(target, _ =>
        {
            var channel = Channel.CreateUnbounded<QueueItem>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

            // Fire-and-forget background worker – dit is de juiste syntax:
            Task ignoredTask = Task.Run(() => ProcessQueueAsync(target, channel.Reader), CancellationToken.None);

            _logger.LogInformation("Dedicated worker started for target {Target}", target);

            return channel;
        });
    }

    /// <summary>
    /// Background worker that processes all queued items for one specific target sequentially.
    /// </summary>
    private async Task ProcessQueueAsync(string target, ChannelReader<QueueItem> reader)
    {
        var (host, port) = ParseTarget(target);

        await foreach (var item in reader.ReadAllAsync())
        {
            var sw = Stopwatch.StartNew();

            try
            {
                using var client = new TcpClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12)); // connect + overall timeout

                await client.ConnectAsync(host, port, cts.Token);

                // Set socket timeouts to prevent indefinite hangs
                client.ReceiveTimeout = (int)TimeSpan.FromMinutes(3).TotalMilliseconds;
                client.SendTimeout = (int)TimeSpan.FromMinutes(3).TotalMilliseconds;

                await using var stream = client.GetStream();

                await WriteFramedAsync(stream, item.Request, cts.Token);
                var response = await ReadFramedAsync(stream, cts.Token);

                item.ResponseTcs.SetResult(response);

                _logger.LogDebug("Forwarded {Bytes} bytes to {Target} in {Ms}ms", response.Length, target, sw.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (!reader.Completion.IsCompleted)
            {
                item.ResponseTcs.SetException(new TimeoutException($"Target {target} timed out"));
                _logger.LogWarning("Timeout while forwarding to {Target} after {Ms}ms", target, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                item.ResponseTcs.SetException(ex);
                _logger.LogWarning(ex, "Failed to forward to {Target} after {Ms}ms", target, sw.ElapsedMilliseconds);
            }
        }
    }

    private static (string host, int port) ParseTarget(string target)
    {
        var parts = target.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var port))
            throw new FormatException($"Invalid target format: {target}. Expected host:port");

        return (parts[0], port);
    }

    private static async Task WriteFramedAsync(NetworkStream stream, byte[] data, CancellationToken ct)
    {
        var len = IPAddress.HostToNetworkOrder(data.Length);
        var lenBytes = BitConverter.GetBytes(len);
        await stream.WriteAsync(lenBytes, ct);
        await stream.WriteAsync(data, ct);
    }

    private static async Task<byte[]> ReadFramedAsync(NetworkStream stream, CancellationToken ct)
    {
        var lenBytes = new byte[4];
        await stream.ReadExactlyAsync(lenBytes, ct);
        int length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(lenBytes));

        if (length <= 0 || length > 50_000_000)
            throw new IOException($"Invalid frame length received: {length}");

        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, ct);
        return buffer;
    }
}