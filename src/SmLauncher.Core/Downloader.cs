using System.Net.Http.Headers;

namespace SmLauncher.Core;

/// <summary>Where a download stands: bytes done of the total, the speed over the last seconds, the time left, and a note while it waits to retry.</summary>
public sealed record DownloadProgress(long Done, long Total, double BytesPerSecond, TimeSpan? Remaining, int Failures, string? Note)
{
    public double Fraction => Total > 0 ? Math.Clamp((double)Done / Total, 0, 1) : 0;
}

/// <summary>The download gave up: after the last retry, or on an answer no retry can fix (the file is gone, it changed size). The .part file is kept.</summary>
public sealed class DownloadFailedException(string message, Exception? inner = null) : Exception(message, inner);

sealed class RetryableException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class DownloadOptions
{
    /// <summary>Failures in a row (with no byte gained between them) before the download gives up.</summary>
    public int MaxFailures { get; init; } = 5;
    /// <summary>Wait before the n-th retry (n from 1): 2, 4, 8, 16, 30 s.</summary>
    public Func<int, TimeSpan> Backoff { get; init; } = n => TimeSpan.FromSeconds(Math.Min(30, 2 * Math.Pow(2, n - 1)));
    /// <summary>No byte for this long: the connection is treated as broken (and retried).</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = (t, ct) => Task.Delay(t, ct);
    public TimeProvider Time { get; init; } = TimeProvider.System;
    /// <summary>A hard cap on attempts, so a connection that drops after every few bytes cannot loop for ever.</summary>
    public int MaxAttempts { get; init; } = 200;
    /// <summary>No answer (status and headers) for this long: the request is treated as failed and retried like a broken connection.</summary>
    public TimeSpan HeaderTimeout { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>Free bytes on a path's drive (tests replace it).</summary>
    public Func<string, long> FreeBytes { get; init; } = DiskSpace.DefaultFreeBytes;
}

/// <summary>What to do with an answer to a (possibly ranged) download request, given the bytes already in the .part file.</summary>
public enum RangeAction { Append, StartOver, Complete, DiscardAndRetry, Retry, Fail }

public readonly record struct RangeDecision(RangeAction Action, string Reason);

/// <summary>The HTTP Range resume rules, engine-free and tested on their own.</summary>
public static class RangeResume
{
    /// <summary>The Range header for a .part file of <paramref name="have"/> bytes (null: ask for the whole file).</summary>
    public static RangeHeaderValue? RequestRange(long have) => have > 0 ? new RangeHeaderValue(have, null) : null;

    public static RangeDecision Decide(int status, long have, long size, ContentRangeHeaderValue? contentRange, long? contentLength)
    {
        switch (status)
        {
            case 206:
                if (contentRange is null || !contentRange.HasRange || contentRange.From is null)
                    return new(RangeAction.DiscardAndRetry, "the server's partial answer had no usable range");
                if (contentRange.HasLength && contentRange.Length != size)
                    return new(RangeAction.Fail, "the file on GitHub is not the size the release lists; look for updates again");
                if (contentRange.From != have)
                    return new(RangeAction.DiscardAndRetry, "the server resumed at the wrong place");
                return new(RangeAction.Append, "resuming");
            case 200:
                if (contentLength is long l && l != size)
                    return new(RangeAction.Fail, "the file on GitHub is not the size the release lists; look for updates again");
                return new(RangeAction.StartOver, have > 0 ? "the server cannot resume; starting again" : "downloading");
            case 416:
                return have == size ? new(RangeAction.Complete, "already complete") : new(RangeAction.DiscardAndRetry, "the server refused the resume point");
            case 404 or 410:
                return new(RangeAction.Fail, "the download is no longer on GitHub (a newer build may have replaced it); look for updates again");
            case 403 or 408 or 429 or >= 500:
                return new(RangeAction.Retry, $"GitHub answered {status}");
            default:
                return new(RangeAction.Fail, $"GitHub answered {status}");
        }
    }
}

/// <summary>
/// Downloads one file into a .part file: resumes with HTTP Range from what is already there, retries network errors with backoff (the failure count
/// resets whenever bytes arrive), reports progress, and leaves the .part file in place on cancel or failure so the next run resumes. It never decides
/// that a file is good: the caller checks the hash.
/// </summary>
public sealed class Downloader(SafeHttp http, DownloadOptions? options = null)
{
    readonly DownloadOptions _o = options ?? new DownloadOptions();

    public async Task DownloadAsync(Uri url, string partPath, long size, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(partPath))!);
        DiskSpace.Require(partPath, size - Math.Min(size, Length(partPath)), "download the update", _o.FreeBytes);
        var meter = new SpeedMeter(_o.Time);
        int failures = 0;
        for (int attempt = 1; ; attempt++)
        {
            long before = Length(partPath);
            try
            {
                await AttemptAsync(url, partPath, size, meter, failures, progress, ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) when (DiskSpace.IsDiskFull(e)) { throw DiskSpace.Wrap(e, "save the download"); }      // not the network: no retry
            catch (Exception e) when (IsRetryable(e))
            {
                long after = Length(partPath);
                failures = after > before ? 1 : failures + 1;
                if (failures >= _o.MaxFailures || attempt >= _o.MaxAttempts)
                    throw new DownloadFailedException($"The download failed {failures} times in a row ({Describe(e)}). Check your internet connection and press Update to try again; the {Mb(after)} already downloaded are kept.", e);
                var wait = _o.Backoff(failures);
                progress?.Report(new DownloadProgress(after, size, 0, null, failures, $"Connection problem ({Describe(e)}). Trying again in {Math.Ceiling(wait.TotalSeconds):0} s (try {failures + 1} of {_o.MaxFailures})."));
                await _o.Delay(wait, ct).ConfigureAwait(false);
            }
        }
    }

    async Task AttemptAsync(Uri url, string part, long size, SpeedMeter meter, int failures, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        long have = Length(part);
        if (have > size) { File.Delete(part); have = 0; }
        if (have == size) return;                           // complete already; the hash check decides whether it is good
        using var headers = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headers.CancelAfter(_o.HeaderTimeout);
        HttpResponseMessage answer;
        try
        {
            answer = await http.SendAsync(u =>
            {
                var r = new HttpRequestMessage(HttpMethod.Get, u);
                r.Headers.Accept.ParseAdd("application/octet-stream");
                r.Headers.Range = RangeResume.RequestRange(have);
                return r;
            }, url, headers.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RetryableException($"no answer from GitHub for {_o.HeaderTimeout.TotalSeconds:0} s");
        }
        headers.CancelAfter(Timeout.InfiniteTimeSpan);      // the body has its own stall timeout
        using var response = answer;
        var decision = RangeResume.Decide((int)response.StatusCode, have, size, response.Content.Headers.ContentRange, response.Content.Headers.ContentLength);
        switch (decision.Action)
        {
            case RangeAction.Complete: return;
            case RangeAction.Fail: throw new DownloadFailedException($"The download stopped: {decision.Reason}.");
            case RangeAction.Retry: throw new RetryableException(decision.Reason);
            case RangeAction.DiscardAndRetry: File.Delete(part); throw new RetryableException(decision.Reason);
        }
        bool append = decision.Action == RangeAction.Append;
        long done = append ? have : 0;
        bool overrun = false;
        meter.Reset(done);
        await using (var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var fs = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, FileOptions.Asynchronous))
        {
            var buffer = new byte[1 << 16];
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            long lastReport = 0;
            while (true)
            {
                stall.CancelAfter(_o.StallTimeout);
                int n;
                try { n = await body.ReadAsync(buffer, stall.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new RetryableException($"no data for {_o.StallTimeout.TotalSeconds:0} s"); }
                if (n == 0) break;
                if (done + n > size) { overrun = true; break; }
                await fs.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                long now = _o.Time.GetTimestamp();
                if (progress is not null && (_o.Time.GetElapsedTime(lastReport, now) >= TimeSpan.FromMilliseconds(100) || done == size))
                {
                    lastReport = now;
                    var (speed, left) = meter.Add(done, size);
                    progress.Report(new DownloadProgress(done, size, speed, left, failures, null));
                }
            }
            await fs.FlushAsync(ct).ConfigureAwait(false);
            fs.Flush(flushToDisk: true);
        }
        if (overrun) { File.Delete(part); throw new RetryableException("the server sent more than the file's size"); }
        if (Length(part) != size) throw new RetryableException("the connection closed early");
    }

    static bool IsRetryable(Exception e) => e is RetryableException or HttpRequestException or IOException or TimeoutException or OperationCanceledException;

    static string Describe(Exception e) => e switch
    {
        RetryableException r => r.Message,
        HttpRequestException h when h.InnerException is { } inner => inner.Message.TrimEnd('.'),
        OperationCanceledException => "the connection timed out",
        _ => e.Message.TrimEnd('.'),
    };

    static long Length(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
    static string Mb(long bytes) => $"{bytes / 1048576.0:0.0} MB";
}

/// <summary>Download speed over the last few seconds, and the time left at that speed.</summary>
public sealed class SpeedMeter(TimeProvider time)
{
    readonly Queue<(long T, long Bytes)> _samples = new();
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    public void Reset(long bytes) { _samples.Clear(); _samples.Enqueue((time.GetTimestamp(), bytes)); }

    public (double BytesPerSecond, TimeSpan? Remaining) Add(long bytes, long total)
    {
        long now = time.GetTimestamp();
        _samples.Enqueue((now, bytes));
        while (_samples.Count > 2 && time.GetElapsedTime(_samples.Peek().T, now) > Window) _samples.Dequeue();
        var first = _samples.Peek();
        double seconds = time.GetElapsedTime(first.T, now).TotalSeconds;
        if (seconds < 0.2) return (0, null);
        double speed = (bytes - first.Bytes) / seconds;
        if (speed <= 0) return (0, null);
        return (speed, TimeSpan.FromSeconds(Math.Max(0, total - bytes) / speed));
    }
}
