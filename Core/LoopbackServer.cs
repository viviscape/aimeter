using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace AIMeter.Core;

/// <summary>
/// Minimal localhost HTTP listener that catches a single OAuth redirect.
/// Uses raw sockets so it needs no URL ACL / admin rights, and listens on both 127.0.0.1 and ::1
/// because browsers may resolve "localhost" to either.
/// </summary>
internal sealed class LoopbackServer : IDisposable
{
    private readonly List<TcpListener> _listeners = new();

    public int Port { get; }

    public LoopbackServer(int port)
    {
        var v4 = new TcpListener(IPAddress.Loopback, port);
        v4.Start();
        _listeners.Add(v4);
        Port = ((IPEndPoint)v4.LocalEndpoint).Port;
        try
        {
            var v6 = new TcpListener(IPAddress.IPv6Loopback, Port);
            v6.Start();
            _listeners.Add(v6);
        }
        catch (SocketException) { /* IPv6 unavailable; IPv4 is enough */ }
    }

    public async Task<NameValueCollection> WaitForCallbackAsync(string path, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<NameValueCollection>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        foreach (var l in _listeners) _ = AcceptLoopAsync(l, path, tcs, ct);
        return await tcs.Task.ConfigureAwait(false);
    }

    private static async Task AcceptLoopAsync(TcpListener listener, string path, TaskCompletionSource<NameValueCollection> tcs, CancellationToken ct)
    {
        while (!tcs.Task.IsCompleted)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch { return; }

            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 8192, leaveOpen: true);
                    var requestLine = await reader.ReadLineAsync(ct).ConfigureAwait(false) ?? "";
                    string? header;
                    while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync(ct).ConfigureAwait(false))) { }

                    var parts = requestLine.Split(' ');
                    var target = parts.Length >= 2 ? parts[1] : "/";
                    var q = target.IndexOf('?');
                    var reqPath = q >= 0 ? target[..q] : target;

                    if (!string.Equals(reqPath, path, StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteAsync(stream, 404, "Not found", ct).ConfigureAwait(false);
                        continue;
                    }

                    var query = HttpUtility.ParseQueryString(q >= 0 ? target[(q + 1)..] : "");
                    var failed = query["error"] is not null;
                    await WriteAsync(stream, 200, Page(failed
                        ? "Sign-in was not completed. You can close this tab and try again from AI Meter."
                        : "You're signed in to AI Meter. You can close this tab."), ct).ConfigureAwait(false);
                    tcs.TrySetResult(query);
                }
            }
            catch { /* ignore a malformed or dropped connection and keep waiting */ }
        }
    }

    private static async Task WriteAsync(Stream stream, int status, string body, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\n" +
                   "Content-Type: text/html; charset=utf-8\r\n" +
                   $"Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string Page(string message) =>
        "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>AI Meter</title>" +
        "<style>body{font-family:'Segoe UI',sans-serif;background:#1b1b1f;color:#f2f2f2;display:grid;place-items:center;height:100vh;margin:0}" +
        "div{padding:32px 40px;border-radius:14px;background:#26262c;font-size:16px}</style></head>" +
        $"<body><div>{WebUtility.HtmlEncode(message)}</div></body></html>";

    public void Dispose()
    {
        foreach (var l in _listeners)
        {
            try { l.Stop(); } catch { }
        }
    }
}
