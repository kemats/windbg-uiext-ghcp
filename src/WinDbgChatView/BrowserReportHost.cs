using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace WinDbgChatView;

internal sealed class BrowserReportHost : IDisposable
{
    private static readonly TimeSpan ReportLifetime = TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<string, Report> _reports = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _startLock = new();
    private TcpListener? _listener;
    private Task? _serverTask;
    private int _disposed;
    private int _port;

    public System.Diagnostics.ProcessStartInfo CreateStartInfo(string html)
    {
        var url = CreateReportUrl(html);
        return new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true };
    }

    public System.Diagnostics.ProcessStartInfo CreateImmersiveReaderStartInfo(string html, string edgeExecutable)
    {
        ArgumentException.ThrowIfNullOrEmpty(edgeExecutable);
        var startInfo = new System.Diagnostics.ProcessStartInfo(edgeExecutable) { UseShellExecute = true };
        startInfo.ArgumentList.Add("read:" + CreateReportUrl(html));
        return startInfo;
    }

    private string CreateReportUrl(string html)
    {
        ArgumentException.ThrowIfNullOrEmpty(html);
        EnsureStarted();
        PruneReports();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _reports[token] = new Report(html, DateTimeOffset.UtcNow.Add(ReportLifetime));
        return $"http://127.0.0.1:{_port}/{token}/report.html";
    }

    private void EnsureStarted()
    {
        if (_listener is not null) return;
        lock (_startLock)
        {
            if (_listener is not null) return;
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            _port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _listener = listener;
            _serverTask = Task.WhenAll(ServeAsync(listener, _shutdown.Token), PruneAsync(_shutdown.Token));
        }
    }

    private async Task PruneAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false)) PruneReports();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task ServeAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = HandleAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        catch (SocketException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            if (client.Client.RemoteEndPoint is not IPEndPoint remote || !IPAddress.IsLoopback(remote.Address)) return;
            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                var requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (requestLine is null || requestLine.Length > 8192) return;
                string? host = null;
                var headerBytes = requestLine.Length;
                while (true)
                {
                    var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null) return;
                    headerBytes += line.Length;
                    if (headerBytes > 32768) return;
                    if (line.Length == 0) break;
                    var separator = line.IndexOf(':');
                    if (separator > 0 && line[..separator].Equals("Host", StringComparison.OrdinalIgnoreCase))
                        host = line[(separator + 1)..].Trim();
                }

                var parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                var expectedHost = $"127.0.0.1:{_port}";
                if (parts.Length != 3 || parts[0] != "GET" || parts[2] != "HTTP/1.1" || host != expectedHost)
                {
                    await WriteResponseAsync(stream, 400, "Bad Request", null, cancellationToken).ConfigureAwait(false);
                    return;
                }

                var pathParts = parts[1].Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (pathParts.Length != 2 || pathParts[1] != "report.html" || !_reports.TryGetValue(pathParts[0], out var report)
                    || report.ExpiresAt <= DateTimeOffset.UtcNow)
                {
                    _reports.TryRemove(pathParts.ElementAtOrDefault(0) ?? "", out _);
                    await WriteResponseAsync(stream, 404, "Not Found", null, cancellationToken).ConfigureAwait(false);
                    return;
                }
                await WriteResponseAsync(stream, 200, "OK", report.Html, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) { }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (SocketException) { }
        }
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int status, string reason, string? html,
        CancellationToken cancellationToken)
    {
        var body = html is null ? [] : Encoding.UTF8.GetBytes(html);
        var headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; img-src data:\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            "X-Frame-Options: DENY\r\n" +
            "Referrer-Policy: no-referrer\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(headers, cancellationToken).ConfigureAwait(false);
        if (body.Length > 0) await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }

    private void PruneReports()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var report in _reports)
            if (report.Value.ExpiresAt <= now) _reports.TryRemove(report.Key, out _);
        foreach (var report in _reports.OrderBy(item => item.Value.ExpiresAt).Take(Math.Max(0, _reports.Count - 31)))
            _reports.TryRemove(report.Key, out _);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        _listener?.Stop();
        _reports.Clear();
        _shutdown.Dispose();
    }

    private sealed record Report(string Html, DateTimeOffset ExpiresAt);
}