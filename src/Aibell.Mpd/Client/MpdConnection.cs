using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Aibell.Mpd.Client;

/// <summary>One answer from MPD: the <c>key: value</c> lines in order and, for commands that return one, the binary payload.</summary>
public sealed record MpdResponse(IReadOnlyList<KeyValuePair<string, string>> Pairs, byte[]? Binary = null)
{
    public static MpdResponse Empty { get; } = new([]);

    /// <summary>The first value of a key, or <c>null</c>.</summary>
    public string? First(string key) => Pairs.FirstOrDefault(pair => pair.Key == key).Value;
}

/// <summary>
/// A connection to an MPD server. Commands run one at a time; <see cref="IdleAsync"/> holds the connection until something changes,
/// so use a connection of its own for it.
/// </summary>
public sealed class MpdConnection : IAsyncDisposable
{
    private const int MaximumLine = 1 << 20;
    private const int MaximumBinary = 16 << 20;

    private readonly Stream stream;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly byte[] buffer = new byte[16 * 1024];
    private int start;
    private int end;
    private bool disposed;

    private MpdConnection(Stream stream) => this.stream = stream;

    /// <summary>The protocol version from the greeting, for example <c>0.23.0</c>.</summary>
    public string ProtocolVersion { get; private set; } = string.Empty;

    /// <summary>Connects over TCP or a local socket and reads the greeting. A password in the endpoint is sent.</summary>
    public static async Task<MpdConnection> ConnectAsync(MpdEndpoint endpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        Socket socket = endpoint.IsLocalSocket
            ? new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
            : new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            if (endpoint.IsLocalSocket)
            {
                string path = endpoint.Host.StartsWith('@') ? "\0" + endpoint.Host[1..] : endpoint.Host;
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellationToken).ConfigureAwait(false);
            }
            else await socket.ConnectAsync(endpoint.Host, endpoint.Port, cancellationToken).ConfigureAwait(false);
            return await OpenAsync(new NetworkStream(socket, ownsSocket: true), endpoint.Password, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Starts the conversation on an open stream: reads the greeting and sends the password when there is one.</summary>
    public static async Task<MpdConnection> OpenAsync(Stream stream, string? password = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        MpdConnection connection = new(stream);
        try
        {
            string greeting = await connection.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (!greeting.StartsWith("OK MPD ", StringComparison.Ordinal))
                throw new MpdProtocolException($"This is not an MPD server (it said: {Truncate(greeting)}).");
            connection.ProtocolVersion = greeting["OK MPD ".Length..].Trim();
            if (!string.IsNullOrEmpty(password))
                await connection.SendAsync(MpdCommand.Format("password", password), cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static string Truncate(string text) => text.Length > 40 ? text[..40] + "…" : text;

    /// <summary>Sends one command line and reads the answer.</summary>
    /// <exception cref="MpdProtocolException">MPD answered with an error.</exception>
    public async Task<MpdResponse> SendAsync(string command, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(command);
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAsync(command, cancellationToken).ConfigureAwait(false);
            return await ReadResponseAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Waits until one of the subsystems (all, when none are named) changes and returns their names. Cancelling the token ends the wait
    /// cleanly with an empty list.
    /// </summary>
    public async Task<IReadOnlyList<string>> IdleAsync(IEnumerable<string>? subsystems = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string command = subsystems is null ? "idle" : MpdCommand.Format("idle", [.. subsystems]);
            await WriteAsync(command, cancellationToken).ConfigureAwait(false);
            using CancellationTokenRegistration registration = cancellationToken.Register(() => _ = WriteAsync("noidle", CancellationToken.None));
            MpdResponse response = await ReadResponseAsync(CancellationToken.None).ConfigureAwait(false);
            return [.. response.Pairs.Where(pair => pair.Key == "changed").Select(pair => pair.Value)];
        }
        finally
        {
            gate.Release();
        }
    }

    private readonly SemaphoreSlim writeGate = new(1, 1);

    private async Task WriteAsync(string command, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(command + "\n");
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ObjectDisposedException or IOException && cancellationToken == CancellationToken.None)
        {
            // The connection already closed; the reader reports it.
        }
        finally
        {
            writeGate.Release();
        }
    }

    private async Task<MpdResponse> ReadResponseAsync(CancellationToken cancellationToken)
    {
        List<KeyValuePair<string, string>> pairs = [];
        byte[]? binary = null;
        while (true)
        {
            string line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line == "OK") return new MpdResponse(pairs, binary);
            if (line.StartsWith("ACK", StringComparison.Ordinal)) throw MpdProtocolException.Parse(line);
            if (line == "list_OK") continue;
            int colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon < 0)
            {
                if (line.EndsWith(':')) pairs.Add(new KeyValuePair<string, string>(line[..^1], string.Empty));
                continue;
            }
            string key = line[..colon];
            string value = line[(colon + 2)..];
            if (key == "binary")
            {
                if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int length) || length > MaximumBinary)
                    throw new MpdProtocolException("MPD sent a binary block that is too large.");
                binary = await ReadBytesAsync(length, cancellationToken).ConfigureAwait(false);
                byte[] newline = await ReadBytesAsync(1, cancellationToken).ConfigureAwait(false);
                if (newline[0] != (byte)'\n') throw new MpdProtocolException("MPD sent a malformed binary block.");
                continue;
            }
            pairs.Add(new KeyValuePair<string, string>(key, value));
        }
    }

    private async Task<byte[]> ReadBytesAsync(int count, CancellationToken cancellationToken)
    {
        byte[] result = new byte[count];
        int filled = 0;
        while (filled < count)
        {
            if (start == end && !await FillAsync(cancellationToken).ConfigureAwait(false)) throw new EndOfStreamException("MPD closed the connection.");
            int take = Math.Min(count - filled, end - start);
            Buffer.BlockCopy(buffer, start, result, filled, take);
            start += take;
            filled += take;
        }
        return result;
    }

    private async Task<string> ReadLineAsync(CancellationToken cancellationToken)
    {
        MemoryStream? overflow = null;
        while (true)
        {
            int newline = start < end ? Array.IndexOf(buffer, (byte)'\n', start, end - start) : -1;
            if (newline >= 0)
            {
                string text;
                if (overflow is null) text = Encoding.UTF8.GetString(buffer, start, newline - start);
                else
                {
                    overflow.Write(buffer, start, newline - start);
                    text = Encoding.UTF8.GetString(overflow.GetBuffer(), 0, (int)overflow.Length);
                }
                start = newline + 1;
                return text.TrimEnd('\r');
            }
            if (start < end)
            {
                overflow ??= new MemoryStream();
                overflow.Write(buffer, start, end - start);
                if (overflow.Length > MaximumLine) throw new MpdProtocolException("MPD sent a line that is too long.");
                start = end;
            }
            if (!await FillAsync(cancellationToken).ConfigureAwait(false)) throw new EndOfStreamException("MPD closed the connection.");
        }
    }

    private async Task<bool> FillAsync(CancellationToken cancellationToken)
    {
        start = 0;
        end = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        return end > 0;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        await stream.DisposeAsync().ConfigureAwait(false);
    }
}
