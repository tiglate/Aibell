using System.Net;
using System.Text;

namespace Aibell.Playback.Miniaudio.Tests;

/// <summary>What <see cref="HlsTestServer"/> answers for one request.</summary>
internal sealed record HlsReply(byte[] Body, int Status = 200, string? Location = null, string ContentType = "application/octet-stream")
{
    public static HlsReply Playlist(string text) => new(Encoding.UTF8.GetBytes(text), ContentType: "application/vnd.apple.mpegurl");

    public static HlsReply Redirect(string location) => new([], 302, location);

    public static HlsReply Fail(int status) => new([], status);
}

/// <summary>A loopback http server for synthetic HLS playlists and segments. Each path maps to a callback that sees its own request number.</summary>
internal sealed class HlsTestServer : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly Dictionary<string, Func<int, HlsReply>> routes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> counts = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;

    public HlsTestServer()
    {
        int port;
        using (System.Net.Sockets.TcpListener probe = new(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        Root = new Uri($"http://127.0.0.1:{port}/");
        listener.Prefixes.Add(Root.AbsoluteUri);
        listener.Start();
        loop = Task.Run(Serve);
    }

    public Uri Root { get; }

    public Uri Address(string path) => new(Root, path);

    public HlsTestServer Map(string path, byte[] body)
    {
        lock (routes) routes[path] = _ => new HlsReply(body);
        return this;
    }

    public HlsTestServer Map(string path, string playlist)
    {
        lock (routes) routes[path] = _ => HlsReply.Playlist(playlist);
        return this;
    }

    public HlsTestServer Map(string path, Func<int, HlsReply> reply)
    {
        lock (routes) routes[path] = reply;
        return this;
    }

    public int Requests(string path)
    {
        lock (counts) return counts.GetValueOrDefault(path);
    }

    private async Task Serve()
    {
        while (!stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception) { return; }
            try { Respond(context); }
            catch (Exception) { /* the client went away */ }
            finally { context.Response.Close(); }
        }
    }

    private void Respond(HttpListenerContext context)
    {
        string path = context.Request.Url!.AbsolutePath;
        int number;
        lock (counts) number = counts[path] = counts.GetValueOrDefault(path) + 1;
        Func<int, HlsReply>? route;
        lock (routes) routes.TryGetValue(path, out route);
        HlsReply reply = route?.Invoke(number) ?? HlsReply.Fail(404);
        HttpListenerResponse response = context.Response;
        response.StatusCode = reply.Status;
        if (reply.Location is not null) response.RedirectLocation = reply.Location;
        response.ContentType = reply.ContentType;
        response.ContentLength64 = reply.Body.Length;
        response.OutputStream.Write(reply.Body);
    }

    public void Dispose()
    {
        stop.Cancel();
        listener.Close();
        try { loop.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        stop.Dispose();
    }
}
