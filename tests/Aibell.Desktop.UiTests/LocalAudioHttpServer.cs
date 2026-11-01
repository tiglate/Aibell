using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Aibell.Desktop.UiTests;

// Owned loopback server: synthetic fixture bytes only, never an external station.
internal sealed class LocalAudioHttpServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentBag<Task> clients = [];
    private readonly byte[] audio;
    private readonly Task accepting;
    private int requests;
    private int feedRequests;
    private int audioRequests;
    private int notModifiedResponses;
    private readonly bool servePodcastFeed;
    private Task? shutdown;
    public Uri Address { get; }
    public int Requests => Volatile.Read(ref requests);
    public Uri FeedAddress => new(Address, "/feed.xml");
    public int FeedRequests => Volatile.Read(ref feedRequests);
    public int AudioRequests => Volatile.Read(ref audioRequests);
    public int NotModifiedResponses => Volatile.Read(ref notModifiedResponses);
    public const string FeedEntityTag = "\"owned-feed-v1\"";

    public LocalAudioHttpServer(byte[] audio, bool servePodcastFeed = false)
    {
        this.audio = audio;
        this.servePodcastFeed = servePodcastFeed;
        listener.Start();
        Address = new($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/live.mp3");
        accepting = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(lifetime.Token);
                clients.Add(ServeAsync(client));
            }
        }
        catch (Exception exception) when (lifetime.IsCancellationRequested && exception is OperationCanceledException or SocketException) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                NetworkStream stream = client.GetStream();
                using StreamReader reader = new(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                string? request = await reader.ReadLineAsync(timeout.Token);
                long offset = 0;
                string? entityTag = null;
                for (int index = 0; index < 40; index++)
                {
                    string? line = await reader.ReadLineAsync(timeout.Token);
                    if (string.IsNullOrEmpty(line)) break;
                    if (line.StartsWith("If-None-Match:", StringComparison.OrdinalIgnoreCase)) entityTag = line[14..].Trim();
                    if (line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase))
                        long.TryParse(line[13..].Split('-')[0], CultureInfo.InvariantCulture, out offset);
                }
                Interlocked.Increment(ref requests);
                if (servePodcastFeed && request?.StartsWith("GET /feed.xml ", StringComparison.Ordinal) == true)
                {
                    Interlocked.Increment(ref feedRequests);
                    if (entityTag == FeedEntityTag)
                    {
                        Interlocked.Increment(ref notModifiedResponses);
                        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 304 Not Modified\r\nETag: {FeedEntityTag}\r\nConnection: close\r\n\r\n"), timeout.Token);
                        return;
                    }
                    byte[] body = Encoding.UTF8.GetBytes($"""
                        <rss version="2.0"><channel><title>Owned podcast fixture</title><description>Synthetic local audio</description>
                        <item><title>Downloaded fixture episode</title><guid>owned-episode</guid>
                        <enclosure url="{Address.AbsoluteUri}" type="audio/mpeg" length="{audio.Length}" /></item>
                        </channel></rss>
                        """);
                    string feedHeader = $"HTTP/1.1 200 OK\r\nContent-Type: application/rss+xml\r\nContent-Length: {body.Length}\r\nETag: {FeedEntityTag}\r\nLast-Modified: Thu, 01 Oct 2026 10:00:00 GMT\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(feedHeader), timeout.Token);
                    await stream.WriteAsync(body, timeout.Token);
                    return;
                }
                bool valid = request is not null && (request.StartsWith("GET /live.mp3 ", StringComparison.Ordinal) || request.StartsWith("HEAD /live.mp3 ", StringComparison.Ordinal));
                if (!valid || offset < 0 || offset >= audio.Length)
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), timeout.Token);
                    return;
                }
                Interlocked.Increment(ref audioRequests);
                int start = (int)offset;
                string status = start == 0 ? "200 OK" : "206 Partial Content";
                string range = start == 0 ? "" : $"Content-Range: bytes {start}-{audio.Length - 1}/{audio.Length}\r\n";
                string header = $"HTTP/1.1 {status}\r\nContent-Type: audio/mpeg\r\nContent-Length: {audio.Length - start}\r\nAccept-Ranges: bytes\r\nConnection: close\r\n{range}\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), timeout.Token);
                if (request!.StartsWith("GET ", StringComparison.Ordinal)) await stream.WriteAsync(audio.AsMemory(start), timeout.Token);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or SocketException) { }
        }
    }

    public ValueTask DisposeAsync() => new(shutdown ??= ShutdownAsync());

    private async Task ShutdownAsync()
    {
        lifetime.Cancel(); listener.Stop();
        await accepting; await Task.WhenAll(clients);
        lifetime.Dispose();
    }
}
