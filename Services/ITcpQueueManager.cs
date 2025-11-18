using System.Net.Sockets;
using TcpQueueProxy.Options;

namespace TcpQueueProxy.Services
{
    public interface ITcpQueueManager
    {
        /// <summary>
        /// Enqueue a client session for a given target. Only one session
        /// per target will be processed at the same time.
        /// </summary>
        void Enqueue(ListenerConfig listenerConfig, TcpClient client);
    }
}
