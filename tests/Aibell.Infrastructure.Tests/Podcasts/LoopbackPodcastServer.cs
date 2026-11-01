using System.Net;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;

namespace Aibell.Infrastructure.Tests.Podcasts;

internal sealed class LoopbackPodcastServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task serving;
    private int requests;
    public int Requests => Volatile.Read(ref requests);
    public Uri Address { get; }
    public ConcurrentQueue<string> RequestHeaders { get; } = new();

    public LoopbackPodcastServer(Func<NetworkStream, CancellationToken, Task> respond)
    {
        listener.Start();
        Address = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/feed");
        serving = ServeAsync(respond);
    }

    private async Task ServeAsync(Func<NetworkStream, CancellationToken, Task> respond)
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                using TcpClient client = await listener.AcceptTcpClientAsync(lifetime.Token);
                await using NetworkStream stream = client.GetStream();
                try
                {
                    using StreamReader reader = new(stream, Encoding.ASCII, leaveOpen: true);
                    StringBuilder headers = new();
                    while (await reader.ReadLineAsync(lifetime.Token) is { Length: > 0 } line) headers.AppendLine(line);
                    RequestHeaders.Enqueue(headers.ToString());
                    Interlocked.Increment(ref requests);
                    await respond(stream, lifetime.Token);
                }
                catch (IOException exception) when (exception.InnerException is SocketException)
                {
                    // Cancellation tests may disconnect while the fixture is sending headers.
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (IOException) when (lifetime.IsCancellationRequested) { }
        catch (SocketException) when (lifetime.IsCancellationRequested) { }
    }

    public static async Task RespondAsync(NetworkStream stream, byte[] body, string contentType = "application/rss+xml", string status = "200 OK", string extraHeaders = "")
    {
        byte[] headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n{extraHeaders}\r\n");
        await stream.WriteAsync(headers);
        await stream.WriteAsync(body);
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel(); listener.Stop();
        try { await serving; }
        finally { lifetime.Dispose(); }
    }
}
