using System.Net;

namespace Aibell.Playback.Miniaudio.Tests;

/// <summary>A loopback http server for one file: either a range-capable download or a live stream that cannot seek.</summary>
internal sealed class AudioTestServer : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly byte[] content;
    private readonly bool live;
    private readonly bool refuseRanges;
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;

    public AudioTestServer(string path, bool live, bool refuseRanges = false)
    {
        this.refuseRanges = refuseRanges;
        content = File.ReadAllBytes(path);
        this.live = live;
        int port;
        using (System.Net.Sockets.TcpListener probe = new(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        Address = new Uri($"http://127.0.0.1:{port}/track{Path.GetExtension(path)}");
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        loop = Task.Run(Serve);
    }

    public Uri Address { get; }

    public int Requests;

    /// <summary>When set, every request is answered with this status and no body.</summary>
    public int? Status { get; set; }

    /// <summary>Holds each response until the gate opens, so a test can keep a track "opening" for as long as it needs.</summary>
    public ManualResetEventSlim? Gate { get; set; }

    /// <summary>After the first request, range requests are answered with the whole file and status 200, like a server that stopped honouring ranges.</summary>
    public bool StopHonouringRangesAfterFirstRequest { get; set; }

    /// <summary>Sends only this many bytes of the file, then drops the connection.</summary>
    public int? DropAfterBytes { get; set; }

    private async Task Serve()
    {
        while (!stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception) { return; }
            Interlocked.Increment(ref Requests);
            try { Respond(context); }
            catch (Exception) { /* the client went away */ }
            finally { context.Response.Close(); }
        }
    }

    private void Respond(HttpListenerContext context)
    {
        HttpListenerResponse response = context.Response;
        Gate?.Wait(stop.Token);
        if (Status is { } status)
        {
            response.StatusCode = status;
            return;
        }
        if (StopHonouringRangesAfterFirstRequest && Requests > 1 && context.Request.Headers["Range"] is not null)
        {
            response.AddHeader("Accept-Ranges", "bytes");
            response.ContentLength64 = content.Length;
            response.OutputStream.Write(content);
            return;
        }
        if (DropAfterBytes is { } limit)
        {
            response.ContentLength64 = content.Length;
            response.OutputStream.Write(content, 0, limit);
            response.OutputStream.Flush();
            response.Abort();
            return;
        }
        if (refuseRanges && context.Request.Headers["Range"] is not null)
        {
            response.StatusCode = 416;
            return;
        }
        if (refuseRanges)
        {
            response.ContentLength64 = content.Length;
            response.OutputStream.Write(content);
            return;
        }
        if (live)
        {
            response.SendChunked = true;
            response.ContentType = "audio/mpeg";
            response.OutputStream.Write(content);
            return;
        }
        response.AddHeader("Accept-Ranges", "bytes");
        long from = 0;
        string? range = context.Request.Headers["Range"];
        if (range is not null && range.StartsWith("bytes=", StringComparison.Ordinal) && long.TryParse(range.AsSpan(6).TrimEnd('-'), out long parsed))
            from = parsed;
        if (range is null)
        {
            response.ContentLength64 = content.Length;
            response.OutputStream.Write(content);
            return;
        }
        response.StatusCode = 206;
        response.AddHeader("Content-Range", $"bytes {from}-{content.Length - 1}/{content.Length}");
        response.ContentLength64 = content.Length - from;
        response.OutputStream.Write(content, (int)from, (int)(content.Length - from));
    }

    public void Dispose()
    {
        stop.Cancel();
        listener.Close();
        try { loop.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        stop.Dispose();
    }
}
