namespace TcpQueueProxy;

/// <summary>
/// Root configuration section loaded from appsettings.json under the "Proxy" key.
/// </summary>
public class ProxyConfig
{
    /// <summary>
    /// Collection of forwarding rules defining which local port forwards to which remote target.
    /// </summary>
    public required List<ForwardRule> Forwards { get; set; }
}

/// <summary>
/// Represents a single proxy forwarding rule.
/// </summary>
public class ForwardRule
{
    /// <summary>
    /// Local TCP port on which the proxy will listen for incoming connections.
    /// </summary>
    public int ListenPort { get; set; }

    /// <summary>
    /// Remote target in "host:port" or "ip:port" format where messages will be forwarded.
    /// Multiple ListenPort entries may share the same Target → they automatically share the same serialized queue.
    /// </summary>
    public required string Target { get; set; }
}