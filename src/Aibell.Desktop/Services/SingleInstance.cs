using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Aibell.Desktop.Services;

/// <summary>
/// Keeps one Aibell per data directory. The first process holds an exclusive lock file for its lifetime (the operating system
/// releases it on a crash) and listens on a user-only named pipe. A later launch passes the files it was given over that pipe and exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const int MaxMessageBytes = 256 * 1024;

    private readonly FileStream lockFile;
    private readonly string pipeName;
    private readonly CancellationTokenSource stopping = new();
    private readonly object gate = new();
    private readonly List<string> pending = [];
    private Action<IReadOnlyList<string>>? handler;
    private Task? listener;

    private SingleInstance(FileStream lockFile, string pipeName)
    {
        this.lockFile = lockFile;
        this.pipeName = pipeName;
    }

    public static string PipeNameFor(string dataDirectory) =>
        "aibell-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataDirectory))))[..16];

    /// <summary>Returns null when another process already owns this data directory.</summary>
    public static SingleInstance? TryBecomePrimary(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        FileStream lockFile;
        try
        {
            lockFile = new FileStream(Path.Combine(dataDirectory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
        SingleInstance instance = new(lockFile, PipeNameFor(dataDirectory));
        instance.listener = Task.Run(instance.ListenAsync);
        return instance;
    }

    /// <summary>Sends the lines (file paths; none just asks the window to come forward) to the running instance.</summary>
    public static bool TrySend(string dataDirectory, IEnumerable<string> lines, TimeSpan timeout)
    {
        string message = string.Join('\n', lines);
        DateTime deadline = DateTime.UtcNow + timeout;
        do
        {
            try
            {
                using NamedPipeClientStream client = new(".", PipeNameFor(dataDirectory), PipeDirection.Out, PipeOptions.CurrentUserOnly);
                client.Connect(500);
                client.Write(Encoding.UTF8.GetBytes(message));
                return true;
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        } while (DateTime.UtcNow < deadline);
        return false;
    }

    /// <summary>Messages that arrived before a handler was set are delivered to it first.</summary>
    public void SetHandler(Action<IReadOnlyList<string>> value)
    {
        string[] queued;
        lock (gate)
        {
            handler = value;
            queued = [.. pending];
            pending.Clear();
        }
        if (queued.Length > 0) value(queued);
    }

    private async Task ListenAsync()
    {
        NamedPipeServerStream? server = null;
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                server = new NamedPipeServerStream(pipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(stopping.Token).ConfigureAwait(false);
                NamedPipeServerStream connection = server;
                server = null;
                _ = ReceiveAsync(connection);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
        finally
        {
            server?.Dispose();
        }
    }

    private async Task ReceiveAsync(NamedPipeServerStream connection)
    {
        try
        {
            using (connection)
            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                using MemoryStream buffer = new();
                byte[] chunk = new byte[4096];
                int read;
                while ((read = await connection.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > MaxMessageBytes) return;
                    buffer.Write(chunk, 0, read);
                }
                string[] lines = Encoding.UTF8.GetString(buffer.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                Action<IReadOnlyList<string>>? target;
                lock (gate)
                {
                    target = handler;
                    if (target is null) pending.AddRange(lines);
                }
                target?.Invoke(lines);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        stopping.Cancel();
        try { listener?.Wait(TimeSpan.FromSeconds(1)); }
        catch (AggregateException) { }
        lockFile.Dispose();
        stopping.Dispose();
    }
}
