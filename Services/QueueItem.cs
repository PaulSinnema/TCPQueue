using System.Threading.Tasks;

namespace TcpQueueProxy;

/// <summary>
/// Represents a single request that is queued for a specific target.
/// </summary>
public class QueueItem
{
    /// <summary>
    /// Raw request payload received from the client.
    /// </summary>
    public required byte[] Request { get; set; }

    /// <summary>
    /// Task completion source used to deliver the response back to the waiting client.
    /// </summary>
    public required TaskCompletionSource<byte[]> ResponseTcs { get; set; }
}