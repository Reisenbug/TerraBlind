using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TerraBlind;

public sealed class VanillaWorkerConnection : IDisposable
{
    Process process;
    TcpClient client;
    StreamReader reader;
    StreamWriter writer;
    readonly SemaphoreSlim serial = new(1);
    readonly SemaphoreSlim writes = new(1);
    long nextRequest;
    TextWriter log;
    readonly object lifetime = new();
    bool started, disposed;
    public JsonObject StartupTiming { get; } = new();
    public bool Alive => !disposed && process != null && started && !process.HasExited;

    public static async Task<VanillaWorkerConnection> Start(byte[] package, string world, CancellationToken cancellation)
    {
        var worker = new VanillaWorkerConnection();
        var timer = Stopwatch.StartNew();
        try
        {
            string root = Path.Combine(Path.GetTempPath(), "terrablind-vnav-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "Mods"));
            await File.WriteAllBytesAsync(Path.Combine(root, "Mods", "Forecast.tmod"), package, cancellation);
            await File.WriteAllTextAsync(Path.Combine(root, "Mods", "enabled.json"), "[\"Forecast\"]", cancellation);
            File.Copy(world, Path.Combine(root, "World.wld"));
            worker.StartupTiming["files_ready_ms"] = timer.Elapsed.TotalMilliseconds;
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            string assembly = typeof(Terraria.Main).Assembly.Location;
            var info = new ProcessStartInfo(Environment.ProcessPath) { WorkingDirectory = Path.GetDirectoryName(assembly),
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { assembly, "-server", "-nosteam", "-tmlsavedirectory", root, "-world", Path.Combine(root, "World.wld"),
                "-vanillaforecastport", port.ToString() }) info.ArgumentList.Add(arg);
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            worker.log = TextWriter.Synchronized(new StreamWriter(Path.Combine(root, "worker.log")) { AutoFlush = true });
            worker.process = new Process { StartInfo = info, EnableRaisingEvents = true };
            worker.process.OutputDataReceived += (_, output) =>
            {
                if (output.Data == null) return;
                lock (worker.lifetime)
                {
                    double elapsed = timer.Elapsed.TotalMilliseconds;
                    worker.log?.WriteLine($"[{elapsed:0.000} ms] {output.Data}");
                    string stage = output.Data.StartsWith("Finding Mods") ? "mods_begin_ms"
                        : output.Data.StartsWith("Loading world data") ? "world_begin_ms"
                        : output.Data.StartsWith("Settling liquids") ? "liquids_begin_ms"
                        : output.Data.Contains("Running engine preload") ? "preload_begin_ms"
                        : output.Data.Contains("VANILLA_FORECAST_READY") ? "ready_ms" : null;
                    if (stage != null && !worker.StartupTiming.ContainsKey(stage)) worker.StartupTiming[stage] = elapsed;
                }
                if (output.Data.Contains("VANILLA_FORECAST_READY")) ready.TrySetResult(true);
            };
            worker.process.ErrorDataReceived += (_, output) => { if (output.Data != null) lock (worker.lifetime) worker.log?.WriteLine(output.Data); };
            worker.process.Exited += (_, _) => ready.TrySetException(new IOException("推演进程退出：" + root + "/worker.log"));
            worker.process.Start(); worker.started = true; worker.process.BeginOutputReadLine(); worker.process.BeginErrorReadLine();
            using var registration = cancellation.Register(worker.Dispose);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(90), cancellation);
            worker.client = new TcpClient { NoDelay = true };
            await worker.client.ConnectAsync(IPAddress.Loopback, port, cancellation);
            worker.reader = new StreamReader(worker.client.GetStream());
            worker.writer = new StreamWriter(worker.client.GetStream()) { AutoFlush = true };
            lock (worker.lifetime) worker.StartupTiming["connected_ms"] = timer.Elapsed.TotalMilliseconds;
            return worker;
        }
        catch { worker.client?.Dispose(); worker.Dispose(); throw; }
    }

    async Task Write(JsonObject request)
    {
        await writes.WaitAsync();
        try { await writer.WriteLineAsync(request.ToJsonString()); }
        finally { writes.Release(); }
    }

    async Task Cancel(long id)
    {
        try { await Write(new JsonObject { ["command"] = "cancel", ["request_id"] = id }); }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }

    public async Task<JsonObject> Send(JsonObject request, CancellationToken cancellation = default)
    {
        long queuedAt = Stopwatch.GetTimestamp();
        await serial.WaitAsync(cancellation);
        try
        {
            var timer = Stopwatch.StartNew();
            double queueMs = Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds;
            cancellation.ThrowIfCancellationRequested();
            long id = Interlocked.Increment(ref nextRequest);
            request["request_id"] = id;
            long writeStartedAt = Stopwatch.GetTimestamp();
            await Write(request);
            double writeMs = Stopwatch.GetElapsedTime(writeStartedAt).TotalMilliseconds;
            using var registration = cancellation.Register(() => _ = Cancel(id));
            long responseStartedAt = Stopwatch.GetTimestamp();
            string line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(request["command"]?.GetValue<string>() == "reachability" ? 120 : 20));
            double responseMs = Stopwatch.GetElapsedTime(responseStartedAt).TotalMilliseconds;
            var result = JsonNode.Parse(line ?? throw new IOException("推演连接已断开")).AsObject();
            cancellation.ThrowIfCancellationRequested();
            if (result["request_id"]?.GetValue<long>() != id && result["error"] == null)
                throw new IOException("推演响应与请求不匹配");
            if (result["error"] != null) throw new InvalidOperationException(result["error"].GetValue<string>());
            result["roundtrip_ms"] = timer.Elapsed.TotalMilliseconds;
            result["queue_ms"] = queueMs;
            result["write_ms"] = writeMs;
            result["response_wait_ms"] = responseMs;
            return result;
        }
        finally { serial.Release(); }
    }

    public void Dispose()
    {
        lock (lifetime) { if (disposed) return; disposed = true; }
        client?.Dispose();
        if (process != null)
        {
            if (started && !process.HasExited) process.Kill(true);
            if (started) process.WaitForExit(1000);
            process.Dispose(); process = null;
        }
        lock (lifetime) { log?.Dispose(); log = null; }
    }
}
