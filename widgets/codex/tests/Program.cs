using System.Text.Json;
using Personal.Codex.ExtensionApp;

const string Quota = """
    {"rateLimits":{"limitId":"codex","primary":{"usedPercent":25,"windowDurationMins":10080},
     "secondary":{"usedPercent":0,"windowDurationMins":300,"resetsAt":1893463200}}}
    """;

if (args is ["app-server"])
{
    var mode = Environment.GetEnvironmentVariable("WIDBAR_CODEX_TEST_MODE");
    var marker = Environment.GetEnvironmentVariable("WIDBAR_CODEX_TEST_PID");
    if (marker is not null) File.WriteAllText(marker, Environment.ProcessId.ToString());
    CheckMethod(Console.ReadLine(), "initialize");
    Console.WriteLine(JsonSerializer.Serialize(new { id = Rpc.Id, result = new { } }));
    CheckMethod(Console.ReadLine(), "initialized");
    CheckMethod(Console.ReadLine(), "account/rateLimits/read");
    if (mode == "hang") await Task.Delay(Timeout.InfiniteTimeSpan);
    Console.WriteLine("""{"method":"account/rateLimits/updated","params":{}}""");
    using var quota = JsonDocument.Parse(Quota);
    Console.WriteLine(mode == "error"
        ? JsonSerializer.Serialize(new { id = Rpc.Id, error = new { code = -32000 } })
        : JsonSerializer.Serialize(new { id = Rpc.Id, result = quota.RootElement }));
    return;
}

if (args is ["--live", var executable])
{
    try
    {
        var windows = await CodexUsage.ReadAsync(executable, CancellationToken.None);
        Console.WriteLine($"Live Codex query: {windows.Count} usable window(s)");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Live Codex query failed ({ex.GetType().Name}).");
        Environment.ExitCode = 1;
    }
    return;
}

var parsed = Parse(Quota);
Equal(2, parsed.Count, "window count");
Equal<int?>(300, parsed[0].WindowMinutes, "shorter window first");
Equal(75, parsed[1].RemainingPercent, "remaining percentage");
Equal<double?>(null, parsed[1].RemainingTimeFraction(DateTimeOffset.UtcNow), "no reset");
var resets = parsed[0].ResetsAt!.Value;
Equal<double?>(1, parsed[0].RemainingTimeFraction(resets.AddHours(-8)), "clamp early reset");
Equal<double?>(0.5, parsed[0].RemainingTimeFraction(resets.AddHours(-2.5)), "half a window");
Equal<double?>(0, parsed[0].RemainingTimeFraction(resets), "expired");
Equal(100, parsed[0].RemainingPercent, "percentage independent of countdown");
var single = Parse("""
    {"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":100,"resetsAt":1893463200},"secondary":null}},
     "rateLimits":{"limitId":"codex_other","primary":{"usedPercent":35}}}
    """);
Equal(1, single.Count, "one window");
Equal(0, single[0].RemainingPercent, "real zero");
Equal<double?>(null, single[0].RemainingTimeFraction(resets), "no duration");
Equal(0, Parse("""{"rateLimits":{"limitId":"codex_other","primary":{"usedPercent":12}}}""").Count, "unrelated quota");
Equal(0, Parse("""{"rateLimits":{"primary":{"usedPercent":-1},"secondary":{"usedPercent":101}}}""").Count, "invalid usage");
var crossing = new QuotaWindow(12, 300, resets);
Equal<DateTimeOffset?>(resets, CodexUsage.NextResetRefresh([crossing], [], resets.AddSeconds(-1), resets.AddSeconds(1)),
    "reset crossed by a paused read still triggers refresh");
Equal<DateTimeOffset?>(null, CodexUsage.NextResetRefresh([crossing], [crossing], resets, resets.AddSeconds(1)),
    "response to post-reset read cannot trigger a refresh loop");
Equal<DateTimeOffset?>(resets, CodexUsage.NextResetRefresh([], [crossing], resets.AddSeconds(-1), resets.AddSeconds(1)),
    "newly discovered reset crossed during read still triggers refresh");
Equal<DateTimeOffset?>(null, CodexUsage.NextResetRefresh([crossing], [], resets.AddSeconds(-2), resets.AddSeconds(-1)),
    "discard an old future deadline if the server no longer reports it");
Equal(2, (await CodexUsage.ReadAsync(Environment.ProcessPath!, CancellationToken.None)).Count, "skip notification");
if (OperatingSystem.IsWindows() && GC.TryStartNoGCRegion(32 * 1024 * 1024))
{
    try
    {
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        var handles = current.HandleCount;
        for (int i = 0; i < 8; i++) await CodexUsage.ReadAsync(Environment.ProcessPath!, CancellationToken.None);
        current.Refresh();
        if (current.HandleCount > handles + 24) throw new Exception($"stdio handles survived repeated queries: {handles} → {current.HandleCount}");
    }
    finally { GC.EndNoGCRegion(); }
}
Environment.SetEnvironmentVariable("WIDBAR_CODEX_TEST_MODE", "error");
await ThrowsAsync<IOException>(() => CodexUsage.ReadAsync(Environment.ProcessPath!, CancellationToken.None));

var pidFile = Path.GetTempFileName();
try
{
    Environment.SetEnvironmentVariable("WIDBAR_CODEX_TEST_MODE", "hang");
    Environment.SetEnvironmentVariable("WIDBAR_CODEX_TEST_PID", pidFile);
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
    var pending = CodexUsage.ReadAsync(Environment.ProcessPath!, stop.Token);
    int pid;
    while (!int.TryParse(File.ReadAllText(pidFile), out pid))
    {
        if (pending.IsCompleted) throw new Exception("fake server did not start");
        await Task.Delay(20);
    }
    stop.Cancel();
    await ThrowsAsync<OperationCanceledException>(() => pending);
    try
    {
        using var process = System.Diagnostics.Process.GetProcessById(pid);
        if (!process.HasExited) throw new Exception("child survived cancellation");
    }
    catch (ArgumentException) { } // The child no longer exists.
}
finally
{
    File.Delete(pidFile);
}
Console.WriteLine("Codex quota and stdio checks passed.");

static IReadOnlyList<QuotaWindow> Parse(string json)
{
    using var doc = JsonDocument.Parse(json);
    return CodexUsage.ParseQuota(doc.RootElement);
}

static void Equal<T>(T expected, T actual, string check)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"{check}: expected {expected}, got {actual}");
}

static async Task ThrowsAsync<T>(Func<Task> run) where T : Exception
{
    try { await run(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}

static void CheckMethod(string? line, string expected)
{
    using var message = JsonDocument.Parse(line ?? throw new EndOfStreamException());
    Equal(expected, message.RootElement.GetProperty("method").GetString(), "RPC method");
    if (message.RootElement.TryGetProperty("id", out var id)) Rpc.Id = id.GetInt32();
}

static class Rpc
{
    public static int Id;
}
