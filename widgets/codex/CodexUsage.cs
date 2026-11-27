using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Personal.Codex.ExtensionApp;

internal sealed record QuotaWindow(int RemainingPercent, int? WindowMinutes, DateTimeOffset? ResetsAt)
{
    public double? RemainingTimeFraction(DateTimeOffset now) =>
        WindowMinutes is > 0 && ResetsAt is { } reset
            ? Math.Clamp((reset - now).TotalMinutes / WindowMinutes.Value, 0, 1)
            : null;
}

internal static class CodexUsage
{
    internal static IReadOnlyList<QuotaWindow> ParseQuota(JsonElement result)
    {
        var quota = Property(Property(result, "rateLimitsByLimitId"), "codex");
        if (quota.ValueKind != JsonValueKind.Object)
        {
            quota = Property(result, "rateLimits");
            var id = Property(quota, "limitId");
            if (id.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)
                && (id.ValueKind != JsonValueKind.String || id.GetString() != "codex"))
                return Array.Empty<QuotaWindow>();
        }

        var windows = new List<QuotaWindow>(2);
        foreach (var key in new[] { "primary", "secondary" })
        {
            var window = Property(quota, key);
            var percent = Property(window, "usedPercent");
            if (percent.ValueKind != JsonValueKind.Number || !percent.TryGetInt32(out var used) || used is < 0 or > 100)
                continue;

            var duration = Property(window, "windowDurationMins");
            int? minutes = duration.ValueKind == JsonValueKind.Number
                && duration.TryGetInt32(out var length) && length > 0 ? length : null;
            var timestamp = Property(window, "resetsAt");
            DateTimeOffset? reset = timestamp.ValueKind == JsonValueKind.Number
                && timestamp.TryGetInt64(out var unix) && unix is > 0 and <= 253402300799
                ? DateTimeOffset.FromUnixTimeSeconds(unix) : null;
            windows.Add(new QuotaWindow(100 - used, minutes, reset));
        }

        if (windows.Count == 2 && (windows[0].WindowMinutes ?? int.MaxValue) > (windows[1].WindowMinutes ?? int.MaxValue))
            (windows[0], windows[1]) = (windows[1], windows[0]);
        return windows;
    }

    internal static DateTimeOffset? NextResetRefresh(
        IReadOnlyList<QuotaWindow> previous, IReadOnlyList<QuotaWindow> current,
        DateTimeOffset requestedAt, DateTimeOffset receivedAt)
    {
        DateTimeOffset? earliest = null;
        foreach (var window in previous)
            if (window.ResetsAt is { } reset && reset <= receivedAt) Consider(reset);
        foreach (var window in current)
            if (window.ResetsAt is { } reset) Consider(reset);
        return earliest;

        void Consider(DateTimeOffset reset)
        {
            // A request started at or after the reset already counted as its refresh.
            if (reset > requestedAt && (earliest is null || reset < earliest.Value)) earliest = reset;
        }
    }

    internal static async Task<IReadOnlyList<QuotaWindow>> ReadAsync(string executablePath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executablePath)
            {
                ArgumentList = { "app-server" },
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        if (!process.Start()) throw new IOException("Could not start Codex.");

        var input = process.StandardInput;
        using var output = process.StandardOutput;
        using var errors = process.StandardError;
        var discardErrors = errors.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
        try
        {
            await SendAsync(input, """{"id":1,"method":"initialize","params":{"clientInfo":{"name":"widbar_codex_limits","version":"1.0.0"}}}""", deadline.Token).ConfigureAwait(false);
            await ReceiveAsync(output, 1, deadline.Token).ConfigureAwait(false);
            await SendAsync(input, """{"method":"initialized","params":{}}""", deadline.Token).ConfigureAwait(false);
            await SendAsync(input, """{"id":2,"method":"account/rateLimits/read"}""", deadline.Token).ConfigureAwait(false);
            return ParseQuota(await ReceiveAsync(output, 2, deadline.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Codex App Server did not respond in time.");
        }
        finally
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or OperationCanceledException) { }
            finally
            {
                deadline.Cancel();
                try { await discardErrors.ConfigureAwait(false); }
                catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
                finally
                {
                    try { input.Dispose(); }
                    catch (IOException) { } // The child may already have closed its end.
                }
            }
        }
    }

    private static JsonElement Property(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) ? value : default;

    private static async Task SendAsync(StreamWriter input, string request, CancellationToken token)
    {
        await input.WriteLineAsync(request.AsMemory(), token).ConfigureAwait(false);
        await input.FlushAsync(token).ConfigureAwait(false);
    }

    private static async Task<JsonElement> ReceiveAsync(StreamReader output, int requestId, CancellationToken token)
    {
        while (await output.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            using var response = JsonDocument.Parse(line);
            var id = Property(response.RootElement, "id");
            if (id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var value) || value != requestId
                || Property(response.RootElement, "method").ValueKind != JsonValueKind.Undefined)
                continue;

            var result = Property(response.RootElement, "result");
            if (result.ValueKind != JsonValueKind.Object) throw new IOException("Codex rejected the request.");
            return result.Clone();
        }

        throw new EndOfStreamException("Codex closed the connection.");
    }
}
