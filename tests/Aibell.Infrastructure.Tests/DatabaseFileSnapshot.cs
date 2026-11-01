using System.Security.Cryptography;

namespace Aibell.Infrastructure.Tests;

internal static class DatabaseFileSnapshot
{
    public static async Task<byte[]> HashAsync(string path)
    {
        // SQLite may retain a live writer or a pooled handle on Windows. The
        // verifier is read-only and permits that existing sharing contract.
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return await SHA256.HashDataAsync(stream);
    }
}
