namespace TcpQueueProxy.Options
{
    public class TcpQueueOptions
    {
        public int SessionTimeoutSeconds { get; set; } = 120;
        public List<ListenerConfig> Listeners { get; set; } = new();
    }

    public class ListenerConfig
    {
        public int ListenPort { get; set; }
        public string TargetHost { get; set; } = string.Empty;
        public int TargetPort { get; set; }

        // Optional, only used for logging
        public string? Description { get; set; }

        public string TargetKey => $"{TargetHost}:{TargetPort}";
    }
}
