using IPRangeConnectionChecker;
using Microsoft.Extensions.Configuration;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;

class Program
{
    // Global Configuration Object
    private static AppConfig? _config;

    // Resource Management
    private static SemaphoreSlim? _v2RaySemaphore;
    private static readonly object ConsoleLock = new();
    private static readonly CancellationTokenSource MonitorCts = new();

    // Statistics
    private static int _totalIps = 0;
    private static int _scannedCount = 0;
    private static int _tcpFound = 0;
    private static int _tcpFailed = 0;
    private static int _v2rayChecked = 0;
    private static int _v2rayFailed = 0;
    private static int _aliveFinal = 0;
    private static long _startTimeTicks;

    // Live UI State
    private static volatile string _currentTcpIp = "...";   // آخرین آی‌پی در حال بررسی پورت
    private static volatile string _currentV2RayIp = "..."; // آخرین آی‌پی در حال تست پروکسی
    static async Task Main()
    {
        try
        {
            LoadConfiguration();

            _v2RaySemaphore = new SemaphoreSlim(_config!.Concurrency.MaxV2RayProcesses, _config.Concurrency.MaxV2RayProcesses);
            _startTimeTicks = DateTime.Now.Ticks;

            Console.WriteLine("Reading IPs...");
            var allIps = await LoadIpsAsync();
            _totalIps = allIps.Count;

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"Total IPs to Scan: {_totalIps:N0}");
            Console.WriteLine($"TCP Workers: {_config.Concurrency.MaxConcurrencyTcp} | V2Ray Workers: {_config.Concurrency.MaxV2RayProcesses}");
            Console.WriteLine("Scan Started...");
            Console.WriteLine("------------------------------------------------------------");
            Console.ResetColor();

            if (File.Exists(_config.Paths.OutputFilePath))
                File.Delete(_config.Paths.OutputFilePath);

            var swTotal = Stopwatch.StartNew();

            var channel = Channel.CreateBounded<(IPAddress Ip, string IpString)>(
                new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.Wait }
            );

            // Start Live UI Monitor
            var monitorTask = StartUiMonitorAsync(MonitorCts.Token);

            var producerTask = ProduceTcpResults(allIps, channel.Writer);
            var consumerTask = ConsumeV2RayTests(channel.Reader);

            await producerTask;
            await consumerTask;

            MonitorCts.Cancel();
            try { await monitorTask; } catch { }

            // Clear the status line for final report
            ClearCurrentConsoleLine();

            swTotal.Stop();
            PrintFinalReport(swTotal.Elapsed);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Critical Error: {ex.Message}");
            Console.ResetColor();
        }
    }

    private static void LoadConfiguration()
    {
        _config = new ConfigurationBuilder()
                            .AddJsonFile("appsettings.json", optional: false)
                            .AddEnvironmentVariables()
                            .Build()
                            .Get<AppConfig>()
                         ?? throw new Exception("Failed to load AppConfig");
    }

    // ================== LIVE UI & MONITORING ==================

    static async Task StartUiMonitorAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                UpdateStatusLine();
                // Update fast (every 200ms) for smooth UI
                await Task.Delay(200, token);
            }
            catch (TaskCanceledException) { break; }
        }
    }

    static void UpdateStatusLine()
    {
        var elapsedSeconds = (DateTime.Now.Ticks - _startTimeTicks) / 10_000_000.0;
        if (elapsedSeconds < 1) elapsedSeconds = 1;
        var speed = _scannedCount / elapsedSeconds;
        int queueSize = _tcpFound - _v2rayChecked;
        if (queueSize < 0) queueSize = 0;
        double progress = _totalIps > 0 ? (_scannedCount * 100.0 / _totalIps) : 0;

        lock (ConsoleLock)
        {
            // نمایش همزمان ورودی (TCP) و خروجی (V2Ray)
            var status = $"[{DateTime.Now:HH:mm:ss}] {progress:F1}% | Spd: {speed:F0}/s | Q: {queueSize} | Alive: {_aliveFinal} >> TCP: {_currentTcpIp} | V2Ray: {_currentV2RayIp}";

            int maxWidth = Console.WindowWidth - 1;
            if (status.Length > maxWidth) status = status.Substring(0, maxWidth);

            Console.Write($"\r{status}".PadRight(maxWidth));
        }

        if (_scannedCount % 50 == 0)
        {
            try { Console.Title = $"Scan: {progress:F1}% | Alive: {_aliveFinal}"; } catch { }
        }
    }

    static void ClearCurrentConsoleLine()
    {
        lock (ConsoleLock)
        {
            int currentLineCursor = Console.CursorTop;
            Console.SetCursorPosition(0, Console.CursorTop);
            Console.Write(new string(' ', Console.WindowWidth));
            Console.SetCursorPosition(0, currentLineCursor);
        }
    }

    static void PrintSuccess(string msg)
    {
        lock (ConsoleLock)
        {
            // 1. Clear the current status line
            Console.Write($"\r{new string(' ', Console.WindowWidth - 1)}\r");

            // 2. Print the log
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[✓ FOUND] {DateTime.Now:HH:mm:ss} -> {msg}");
            Console.ResetColor();

            // 3. Status line will be redrawn immediately by the monitor loop or next call
        }
    }

    // ================== WORKERS ==================

    static async Task ProduceTcpResults(List<IPAddress> allIps, ChannelWriter<(IPAddress Ip, string IpString)> writer)
    {
        var pOptions = new ParallelOptions { MaxDegreeOfParallelism = _config!.Concurrency.MaxConcurrencyTcp };
        await Parallel.ForEachAsync(allIps, pOptions, async (ip, ct) =>
        {
            // آپدیت لحظه‌ای بخش TCP
            _currentTcpIp = ip.ToString();

            try
            {
                if (await CheckPortsSequential(ip))
                {
                    Interlocked.Increment(ref _tcpFound);
                    await writer.WriteAsync((ip, ip.ToString()), ct);
                }
                else
                {
                    Interlocked.Increment(ref _tcpFailed);
                }
            }
            catch
            {
                Interlocked.Increment(ref _tcpFailed);
            }
            finally
            {
                Interlocked.Increment(ref _scannedCount);
            }
        });

        writer.Complete();
    }

    static async Task ConsumeV2RayTests(ChannelReader<(IPAddress Ip, string IpString)> reader)
    {
        var tasks = new List<Task>();
        while (await reader.WaitToReadAsync())
        {
            while (reader.TryRead(out var item))
            {
                await _v2RaySemaphore!.WaitAsync();
                tasks.Add(Task.Run(async () =>
                {
                    try { await TestV2RayConnection(item.IpString); }
                    finally
                    {
                        _v2RaySemaphore.Release();
                        Interlocked.Increment(ref _v2rayChecked);
                    }
                }));
            }
        }
        await Task.WhenAll(tasks);
    }

    // ================== V2RAY LOGIC ==================

    static async Task TestV2RayConnection(string ipAddress)
    {
        // <<< این خط جا افتاده بود >>>
        _currentV2RayIp = ipAddress;

        Process? v2ray = null;
        try
        {
            int localPort = GetFreeTcpPort();
            string jsonConfig = CreateHttpV2RayConfig(ipAddress, localPort);

            // Using Memory Config Injection
            v2ray = StartV2RayProcess(jsonConfig);

            if (v2ray == null || v2ray.HasExited)
            {
                Interlocked.Increment(ref _v2rayFailed);
                return;
            }

            if (!await WaitForLocalPort(localPort, _config!.Timeouts.V2RayStartTimeoutMs))
            {
                Interlocked.Increment(ref _v2rayFailed);
                return;
            }

            var sw = Stopwatch.StartNew();
            bool works = await TestThroughHttpProxy(localPort);
            sw.Stop();

            if (works)
            {
                Interlocked.Increment(ref _aliveFinal);
                PrintSuccess($"{ipAddress} | {sw.ElapsedMilliseconds}ms");
                SaveResult(ipAddress);
            }
            else
            {
                Interlocked.Increment(ref _v2rayFailed);
            }
        }
        catch
        {
            Interlocked.Increment(ref _v2rayFailed);
        }
        finally
        {
            CleanupProcess(v2ray);
        }
    }

    static Process? StartV2RayProcess(string jsonConfig)
    {
        try
        {
            string exe = Path.Combine(AppContext.BaseDirectory, _config!.Paths.V2RayExeName);
            if (!File.Exists(exe)) return null;

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "run -c stdin:",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            var p = new Process { StartInfo = psi };
            p.Start();

            using (var writer = p.StandardInput)
            {
                writer.Write(jsonConfig);
                writer.Close();
            }
            return p;
        }
        catch { return null; }
    }

    static string CreateHttpV2RayConfig(string ipAddress, int localPort)
    {
        var v2ray = _config!.V2Ray;
        var config = new
        {
            log = new { loglevel = "none" },
            inbounds = new[]
            {
                new { port = localPort, listen = "127.0.0.1", protocol = "http", settings = new { allowTransparent = false } }
            },
            outbounds = new[]
            {
                new {
                    protocol = "vless",
                    settings = new { vnext = new[] { new { address = ipAddress, port = 443, users = new[] { new { id = v2ray.VlessUuid, encryption = "none" } } } } },
                    streamSettings = new {
                        network = "ws", security = "tls",
                        tlsSettings = new { allowInsecure = true, serverName = v2ray.VlessSni, fingerprint = "chrome" },
                        wsSettings = new { path = v2ray.VlessPath, headers = new { Host = v2ray.VlessHost } }
                    }
                }
            }
        };
        return JsonSerializer.Serialize(config);
    }

    static async Task<bool> TestThroughHttpProxy(int localPort)
    {
        try
        {
            var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://{_config!.Network.LoopbackIp}:{localPort}"),
                UseProxy = true,
                AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
            };
            using var client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromMilliseconds(_config.Timeouts.HttpTestTimeoutMs);
            var resp = await client.GetAsync(_config.Network.TestUrl);
            return resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.NoContent;
        }
        catch { return false; }
    }

    // ================== HELPERS ==================

    static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    static async Task<bool> CheckPortsSequential(IPAddress ip)
    {
        foreach (int port in _config!.Network.TargetTcpPorts)
        {
            if (!await IsPortOpen(ip, port)) return false;
        }
        return true;
    }

    static async Task<bool> IsPortOpen(IPAddress ip, int port)
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(ip, port);
            var finished = await Task.WhenAny(connectTask, Task.Delay(_config!.Timeouts.TcpTimeoutMs));
            return finished == connectTask && client.Connected;
        }
        catch { return false; }
    }

    static async Task<bool> WaitForLocalPort(int port, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                using var client = new TcpClient();
                var task = client.ConnectAsync(IPAddress.Loopback, port);
                if (await Task.WhenAny(task, Task.Delay(50)) == task && client.Connected) return true;
            }
            catch { }
            await Task.Delay(20);
        }
        return false;
    }

    static void CleanupProcess(Process? p)
    {
        try { if (p != null && !p.HasExited) { p.Kill(); p.WaitForExit(100); } }
        catch { }
        finally { p?.Dispose(); }
    }

    static void SaveResult(string ip)
    {
        lock (ConsoleLock)
        {
            File.AppendAllText(_config!.Paths.OutputFilePath, $"{ip}\n");
        }
    }

    static async Task<List<IPAddress>> LoadIpsAsync()
    {
        var list = new List<IPAddress>();
        var path = _config!.Paths.InputFilePath;
        if (!File.Exists(path)) return list;
        var lines = await File.ReadAllLinesAsync(path);
        foreach (var line in lines)
        {
            var s = line.Trim();
            if (string.IsNullOrEmpty(s) || s.StartsWith('#')) continue;
            if (s.Contains('/')) list.AddRange(ExpandCidr(s));
            else if (IPAddress.TryParse(s, out var ip)) list.Add(ip);
        }
        return [.. list.Distinct()];
    }

    static IEnumerable<IPAddress> ExpandCidr(string cidr)
    {
        var parts = cidr.Split('/');
        var baseIp = IPAddress.Parse(parts[0]);
        int prefix = int.Parse(parts[1]);
        uint ip = BitConverter.ToUInt32([.. baseIp.GetAddressBytes().Reverse()], 0);
        uint count = (uint)(1 << (32 - prefix));
        if (count > 65536) count = 65536;
        for (uint i = 1; i < count - 1; i++) yield return new IPAddress([.. BitConverter.GetBytes(ip + i).Reverse()]);
    }

    static void PrintFinalReport(TimeSpan elapsed)
    {
        Console.WriteLine("\n" + new string('=', 60));
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("SCAN COMPLETED SUCCESSFULLY");
        Console.WriteLine(new string('-', 60));
        Console.WriteLine($"Total Time    : {elapsed.TotalMinutes:F2} min");
        Console.WriteLine($"Total Scanned : {_scannedCount:N0}");
        Console.WriteLine($"TCP Open      : {_tcpFound:N0}");
        Console.WriteLine($"TCP Closed    : {_tcpFailed:N0}");
        Console.WriteLine($"V2Ray Working : {_aliveFinal:N0}");
        Console.WriteLine($"V2Ray Failed  : {_v2rayFailed:N0}");
        Console.WriteLine($"Results saved : {Path.GetFullPath(_config!.Paths.OutputFilePath)}");
        Console.ResetColor();
        Console.WriteLine(new string('=', 60));
        Console.WriteLine("\nPress Enter to exit...");
        Console.ReadLine();
    }
}
//using System.Diagnostics;
//using System.Net;
//using System.Net.Sockets;
//using System.Text.Json;
//using System.Threading.Channels;
//using IPRangeConnectionChecker;
//using Microsoft.Extensions.Configuration;

//class Program
//{
//    // Global Configuration Object
//    private static AppConfig? _config;

//    // Resource Management
//    private static SemaphoreSlim? _v2RaySemaphore;
//    private static readonly object ConsoleLock = new();
//    private static readonly CancellationTokenSource MonitorCts = new();

//    // Statistics
//    private static int _totalIps = 0;
//    private static int _scannedCount = 0;
//    private static int _tcpFound = 0;
//    private static int _tcpFailed = 0;
//    private static int _v2rayChecked = 0;
//    private static int _v2rayFailed = 0;
//    private static int _aliveFinal = 0;
//    private static long _startTimeTicks;

//    static async Task Main()
//    {
//        try
//        {
//            // 1. Load Configuration
//            LoadConfiguration();

//            // Initialize Semaphore based on config
//            _v2RaySemaphore = new SemaphoreSlim(_config!.Concurrency.MaxV2RayProcesses, _config.Concurrency.MaxV2RayProcesses);

//            _startTimeTicks = DateTime.Now.Ticks;

//            Console.WriteLine("Reading IPs...");
//            var allIps = await LoadIpsAsync();
//            _totalIps = allIps.Count;

//            Console.ForegroundColor = ConsoleColor.Cyan;
//            Console.WriteLine($"Total IPs to Scan: {_totalIps:N0}");
//            Console.WriteLine($"TCP Workers: {_config.Concurrency.MaxConcurrencyTcp} | V2Ray Workers: {_config.Concurrency.MaxV2RayProcesses}");
//            Console.WriteLine("Scan Started...");
//            Console.WriteLine("------------------------------------");
//            Console.ResetColor();

//            // Clean up old output file
//            if (File.Exists(_config.Paths.OutputFilePath))
//                File.Delete(_config.Paths.OutputFilePath);

//            var swTotal = Stopwatch.StartNew();

//            // Create Bounded Channel to manage memory
//            var channel = Channel.CreateBounded<(IPAddress Ip, string IpString)>(
//                new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.Wait }
//            );

//            // Start Monitoring Task
//            var monitorTask = StartMonitorAsync(MonitorCts.Token);

//            // Start Producer (TCP Scanner)
//            var producerTask = ProduceTcpResults(allIps, channel.Writer);

//            // Start Consumer (V2Ray Tester)
//            var consumerTask = ConsumeV2RayTests(channel.Reader);

//            // Wait for completion
//            await producerTask;
//            await consumerTask;

//            // Stop Monitor
//            MonitorCts.Cancel();
//            try { await monitorTask; } catch { }

//            swTotal.Stop();
//            PrintFinalReport(swTotal.Elapsed);
//        }
//        catch (Exception ex)
//        {
//            Console.ForegroundColor = ConsoleColor.Red;
//            Console.WriteLine($"Critical Error: {ex.Message}");
//            Console.ResetColor();
//        }
//    }

//    private static void LoadConfiguration()
//    {
//        _config = new ConfigurationBuilder()
//                            .AddJsonFile("appsettings.json", optional: false)
//                            .AddEnvironmentVariables()
//                            .Build()
//                            .Get<AppConfig>()
//                         ?? throw new Exception("Failed to load AppConfig");
//    }

//    // ================== MONITORING ==================

//    static async Task StartMonitorAsync(CancellationToken token)
//    {
//        while (!token.IsCancellationRequested)
//        {
//            try
//            {
//                UpdateTitle();
//                PrintHeartbeat();
//                await Task.Delay(10000, token); // 10 seconds heartbeat
//            }
//            catch (TaskCanceledException) { break; }
//        }
//    }

//    static void PrintHeartbeat()
//    {
//        var elapsedSeconds = (DateTime.Now.Ticks - _startTimeTicks) / 10_000_000.0;
//        if (elapsedSeconds < 1) elapsedSeconds = 1;
//        var speed = _scannedCount / elapsedSeconds;

//        int queueSize = _tcpFound - _v2rayChecked;
//        if (queueSize < 0) queueSize = 0;

//        double progress = _totalIps > 0 ? (_scannedCount * 100.0 / _totalIps) : 0;

//        lock (ConsoleLock)
//        {
//            Console.ForegroundColor = ConsoleColor.DarkGray;
//            Console.WriteLine(
//                $"[{DateTime.Now:HH:mm:ss}] " +
//                $"Progress: {progress:F1}% | " +
//                $"Speed: {speed:F0}/s | " +
//                $"TCP Found: {_tcpFound} | " +
//                $"V2Ray Queue: {queueSize} | " +
//                $"ALIVE: {_aliveFinal}"
//            );
//            Console.ResetColor();
//        }
//    }

//    static void UpdateTitle()
//    {
//        double progress = _totalIps > 0 ? (_scannedCount * 100.0 / _totalIps) : 0;
//        int queueSize = _tcpFound - _v2rayChecked;
//        string title = $"Scan: {progress:F1}% | Alive: {_aliveFinal} | V2Ray Queue: {queueSize}";
//        try { Console.Title = title; } catch { }
//    }

//    // ================== WORKERS ==================

//    static async Task ProduceTcpResults(List<IPAddress> allIps, ChannelWriter<(IPAddress Ip, string IpString)> writer)
//    {
//        var pOptions = new ParallelOptions { MaxDegreeOfParallelism = _config!.Concurrency.MaxConcurrencyTcp };
//        await Parallel.ForEachAsync(allIps, pOptions, async (ip, ct) =>
//        {
//            try
//            {
//                if (await CheckPortsSequential(ip))
//                {
//                    Interlocked.Increment(ref _tcpFound);
//                    await writer.WriteAsync((ip, ip.ToString()), ct);
//                }
//                else
//                {
//                    Interlocked.Increment(ref _tcpFailed);
//                }
//            }
//            catch
//            {
//                Interlocked.Increment(ref _tcpFailed);
//            }
//            finally
//            {
//                Interlocked.Increment(ref _scannedCount);
//            }
//        });

//        writer.Complete();
//        lock (ConsoleLock) Console.WriteLine("\n[TCP SCAN FINISHED] Processing remaining V2Ray queue...");
//    }

//    static async Task ConsumeV2RayTests(ChannelReader<(IPAddress Ip, string IpString)> reader)
//    {
//        var tasks = new List<Task>();

//        while (await reader.WaitToReadAsync())
//        {
//            while (reader.TryRead(out var item))
//            {
//                await _v2RaySemaphore!.WaitAsync();

//                var task = Task.Run(async () =>
//                {
//                    try
//                    {
//                        await TestV2RayConnection(item.IpString);
//                    }
//                    finally
//                    {
//                        _v2RaySemaphore.Release();
//                        Interlocked.Increment(ref _v2rayChecked);
//                    }
//                });
//                tasks.Add(task);
//            }
//        }
//        await Task.WhenAll(tasks);
//    }

//    // ================== V2RAY LOGIC ==================

//    static async Task TestV2RayConnection(string ipAddress)
//    {
//        Process? v2ray = null;
//        try
//        {
//            int localPort = GetFreeTcpPort();

//            // ساخت جیسون کانفیگ (بدون تغییر)
//            string jsonConfig = CreateHttpV2RayConfig(ipAddress, localPort);

//            // اجرای Xray و تزریق کانفیگ مستقیم به حافظه
//            v2ray = StartV2RayProcess(jsonConfig);

//            if (v2ray == null || v2ray.HasExited)
//            {
//                Interlocked.Increment(ref _v2rayFailed);
//                return;
//            }

//            // صبر برای باز شدن پورت
//            if (!await WaitForLocalPort(localPort, _config!.Timeouts.V2RayStartTimeoutMs))
//            {
//                Interlocked.Increment(ref _v2rayFailed);
//                return;
//            }

//            // تست اتصال
//            var sw = Stopwatch.StartNew();
//            bool works = await TestThroughHttpProxy(localPort);
//            sw.Stop();

//            if (works)
//            {
//                Interlocked.Increment(ref _aliveFinal);
//                string msg = $"{ipAddress} | {sw.ElapsedMilliseconds}ms";
//                PrintSuccess(msg);
//                SaveResult(ipAddress);
//            }
//            else
//            {
//                Interlocked.Increment(ref _v2rayFailed);
//            }
//        }
//        catch
//        {
//            Interlocked.Increment(ref _v2rayFailed);
//        }
//        finally
//        {
//            // بستن پروسه
//            CleanupProcess(v2ray);
//        }
//    }

//    static Process? StartV2RayProcess(string jsonConfig)
//    {
//        try
//        {
//            string exe = Path.Combine(AppContext.BaseDirectory, _config!.Paths.V2RayExeName);
//            if (!File.Exists(exe)) return null;

//            var psi = new ProcessStartInfo
//            {
//                FileName = exe,
//                // نکته مهم: به xray می‌گوییم کانفیگ را از stdin بخواند
//                Arguments = "run -c stdin:",
//                UseShellExecute = false,
//                CreateNoWindow = true,
//                RedirectStandardInput = true, // فعال کردن ورودی استاندارد
//                RedirectStandardOutput = true, // بستن خروجی برای جلوگیری از پر شدن بافر
//                RedirectStandardError = true
//            };

//            var p = new Process { StartInfo = psi };
//            p.Start();

//            // نوشتن جیسون در ورودی استاندارد پروسه
//            using (var writer = p.StandardInput)
//            {
//                writer.Write(jsonConfig);
//                // بستن رایتر خیلی مهم است، چون به xray می‌گوید فایل تمام شد
//                writer.Close();
//            }

//            return p;
//        }
//        catch { return null; }
//    }
//    static string CreateHttpV2RayConfig(string ipAddress, int localPort)
//    {
//        var v2ray = _config!.V2Ray;

//        var config = new
//        {
//            log = new { loglevel = "none" },
//            inbounds = new[]
//            {
//                new
//                {
//                    port = localPort,
//                    listen = "127.0.0.1",
//                    protocol = "http", // Using HTTP to avoid .NET SOCKS5 issues
//                    settings = new { allowTransparent = false }
//                }
//            },
//            outbounds = new[]
//            {
//                new
//                {
//                    protocol = "vless",
//                    settings = new
//                    {
//                        vnext = new[]
//                        {
//                            new
//                            {
//                                address = ipAddress,
//                                port = 443,
//                                users = new[] { new { id = v2ray.VlessUuid, encryption = "none" } }
//                            }
//                        }
//                    },
//                    streamSettings = new
//                    {
//                        network = "ws",
//                        security = "tls",
//                        tlsSettings = new
//                        {
//                            allowInsecure = true,
//                            serverName = v2ray.VlessSni,
//                            fingerprint = "chrome"
//                        },
//                        wsSettings = new
//                        {
//                            path = v2ray.VlessPath,
//                            headers = new { Host = v2ray.VlessHost }
//                        }
//                    }
//                }
//            }
//        };

//        return JsonSerializer.Serialize(config);
//    }

//    static async Task<bool> TestThroughHttpProxy(int localPort)
//    {
//        try
//        {
//            var handler = new HttpClientHandler
//            {
//                Proxy = new WebProxy($"http://{_config!.Network.LoopbackIp}:{localPort}"),
//                UseProxy = true,
//                AllowAutoRedirect = false,
//                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
//            };

//            using var client = new HttpClient(handler);
//            client.Timeout = TimeSpan.FromMilliseconds(_config.Timeouts.HttpTestTimeoutMs);

//            var resp = await client.GetAsync(_config.Network.TestUrl);

//            // 204 or Success indicates a working connection
//            return resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.NoContent;
//        }
//        catch { return false; }
//    }

//    // ================== HELPERS ==================

//    static int GetFreeTcpPort()
//    {
//        using var listener = new TcpListener(IPAddress.Loopback, 0);
//        listener.Start();
//        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
//        return port;
//    }

//    static async Task<bool> CheckPortsSequential(IPAddress ip)
//    {
//        foreach (int port in _config!.Network.TargetTcpPorts)
//        {
//            if (!await IsPortOpen(ip, port)) return false;
//        }
//        return true;
//    }

//    static async Task<bool> IsPortOpen(IPAddress ip, int port)
//    {
//        try
//        {
//            using var client = new TcpClient();
//            var connectTask = client.ConnectAsync(ip, port);
//            var finished = await Task.WhenAny(connectTask, Task.Delay(_config!.Timeouts.TcpTimeoutMs));
//            return finished == connectTask && client.Connected;
//        }
//        catch { return false; }
//    }

//    static async Task<bool> WaitForLocalPort(int port, int timeoutMs)
//    {
//        var sw = Stopwatch.StartNew();
//        while (sw.ElapsedMilliseconds < timeoutMs)
//        {
//            try
//            {
//                using var client = new TcpClient();
//                var task = client.ConnectAsync(IPAddress.Loopback, port);
//                var finished = await Task.WhenAny(task, Task.Delay(50));
//                if (finished == task && client.Connected) return true;
//            }
//            catch { }
//            await Task.Delay(20);
//        }
//        return false;
//    }





//    static void CleanupProcess(Process? p)
//    {
//        try
//        {
//            if (p != null && !p.HasExited) { p.Kill(); p.WaitForExit(100); }
//        }
//        catch { }
//        finally { p?.Dispose(); }
//    }



//    static void PrintSuccess(string msg)
//    {
//        lock (ConsoleLock)
//        {
//            Console.ForegroundColor = ConsoleColor.Green;
//            Console.WriteLine($"[✓ FOUND] {DateTime.Now:HH:mm:ss} -> {msg}");
//            Console.ResetColor();
//        }
//    }

//    static void SaveResult(string ip)
//    {
//        lock (ConsoleLock)
//        {
//            File.AppendAllText(_config!.Paths.OutputFilePath, $"{ip}\n");
//        }
//    }

//    static async Task<List<IPAddress>> LoadIpsAsync()
//    {
//        var list = new List<IPAddress>();
//        var path = _config!.Paths.InputFilePath;
//        if (!File.Exists(path)) return list;

//        var lines = await File.ReadAllLinesAsync(path);
//        foreach (var line in lines)
//        {
//            var s = line.Trim();
//            if (string.IsNullOrEmpty(s) || s.StartsWith('#')) continue;
//            if (s.Contains('/')) list.AddRange(ExpandCidr(s));
//            else if (IPAddress.TryParse(s, out var ip)) list.Add(ip);
//        }
//        return [.. list.Distinct()];
//    }

//    static IEnumerable<IPAddress> ExpandCidr(string cidr)
//    {
//        var parts = cidr.Split('/');
//        var baseIp = IPAddress.Parse(parts[0]);
//        int prefix = int.Parse(parts[1]);
//        uint ip = BitConverter.ToUInt32([.. baseIp.GetAddressBytes().Reverse()], 0);
//        uint count = (uint)(1 << (32 - prefix));
//        if (count > 65536) count = 65536;
//        for (uint i = 1; i < count - 1; i++)
//        {
//            yield return new IPAddress([.. BitConverter.GetBytes(ip + i).Reverse()]);
//        }
//    }

//    static void PrintFinalReport(TimeSpan elapsed)
//    {
//        Console.WriteLine("\n" + new string('=', 60));
//        Console.ForegroundColor = ConsoleColor.Yellow;
//        Console.WriteLine("SCAN COMPLETED SUCCESSFULLY");
//        Console.WriteLine(new string('-', 60));
//        Console.WriteLine($"Total Time    : {elapsed.TotalMinutes:F2} min");
//        Console.WriteLine($"Total Scanned : {_scannedCount:N0}");
//        Console.WriteLine($"TCP Open      : {_tcpFound:N0}");
//        Console.WriteLine($"TCP Closed    : {_tcpFailed:N0}");
//        Console.WriteLine($"V2Ray Working : {_aliveFinal:N0}");
//        Console.WriteLine($"V2Ray Failed  : {_v2rayFailed:N0}");
//        Console.WriteLine($"Results saved : {Path.GetFullPath(_config!.Paths.OutputFilePath)}");
//        Console.ResetColor();
//        Console.WriteLine(new string('=', 60));
//        Console.WriteLine("\nPress Enter to exit...");
//        Console.ReadLine();
//    }
//}


