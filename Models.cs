

namespace IPRangeConnectionChecker;

// ================== CONFIGURATION MODELS ==================

public class AppConfig
{
    public PathsConfig Paths { get; set; } = new();
    public ConcurrencyConfig Concurrency { get; set; } = new();
    public TimeoutsConfig Timeouts { get; set; } = new();
    public NetworkConfig Network { get; set; } = new();
    public V2RayConfig V2Ray { get; set; } = new();
}

public class PathsConfig
{
    public string InputFilePath { get; set; } = "ip.txt";
    public string OutputFilePath { get; set; } = "alive_ip.txt";
    public string V2RayExeName { get; set; } = "xray.exe";
}

public class ConcurrencyConfig
{
    public int MaxConcurrencyTcp { get; set; } = 200;
    public int MaxV2RayProcesses { get; set; } = 16;
}

public class TimeoutsConfig
{
    public int TcpTimeoutMs { get; set; } = 1500;
    public int V2RayStartTimeoutMs { get; set; } = 2000;
    public int HttpTestTimeoutMs { get; set; } = 4000;
}

public class NetworkConfig
{
    public int[] TargetTcpPorts { get; set; } = [];
    public string TestUrl { get; set; } = "";
    public string LoopbackIp { get; set; } = "127.0.0.1";
}

public class V2RayConfig
{
    public string VlessUuid { get; set; } = "";
    public string VlessSni { get; set; } = "";
    public string VlessHost { get; set; } = "";
    public string VlessPath { get; set; } = "";
}
