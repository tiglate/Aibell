using System.Security.Cryptography;
using NUnit.Framework;

namespace Aibell.Infrastructure.Tests;

[TestFixture]
public sealed class DatabaseFileSnapshotTests
{
    [Test]
    public async Task HashAsync_LiveWriter_AllowsSharedReadWithoutChangingBytes()
    {
        string path = Path.Combine(Path.GetTempPath(), "aibell-shared-hash-" + Guid.NewGuid().ToString("N"));
        byte[] expected = [1, 2, 3, 4, 5];
        try
        {
            await using (FileStream writer = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            {
                await writer.WriteAsync(expected); await writer.FlushAsync();
                Assert.That(await DatabaseFileSnapshot.HashAsync(path), Is.EqualTo(SHA256.HashData(expected)));
            }
            Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(expected));
        }
        finally { File.Delete(path); }
    }
}
