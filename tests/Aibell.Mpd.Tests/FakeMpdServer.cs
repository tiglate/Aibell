using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Aibell.Mpd.Tests;

/// <summary>A scripted MPD on a loopback port: sends the greeting, then answers each command line with whatever the script returns.</summary>
internal sealed class FakeMpdServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    private readonly Func<string, byte[]?> script;

    public FakeMpdServer(Func<string, byte[]?> script, string greeting = "OK MPD 0.23.0\n")
    {
        this.script = script;
        listener.Start();
        loop = Serve(greeting);
    }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public List<string> Received { get; } = [];

    public static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

    public static byte[] Binary(string header, byte[] data, string tail = "OK\n") =>
        [.. Text(header + $"binary: {data.Length}\n"), .. data, .. Text("\n" + tail)];

    private async Task Serve(string greeting)
    {
        try
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(stop.Token);
            NetworkStream stream = client.GetStream();
            await stream.WriteAsync(Text(greeting), stop.Token);
            using StreamReader reader = new(stream, new UTF8Encoding(false));
            while (await reader.ReadLineAsync(stop.Token) is { } line)
            {
                lock (Received) Received.Add(line);
                byte[]? reply = script(line);
                if (reply is null) break;
                if (reply.Length > 0) await stream.WriteAsync(reply, stop.Token);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException or SocketException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        listener.Stop();
        await loop;
        stop.Dispose();
    }
}
