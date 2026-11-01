namespace Aibell.Audio;

/// <summary>Streams such as a live HTTP body claim to seek yet have no length; the decoders treat those as forward-only.</summary>
public static class StreamLength
{
    /// <summary>The stream's length, when it can seek and knows how long it is.</summary>
    public static bool TryGet(Stream stream, out long length)
    {
        length = -1;
        if (!stream.CanSeek) return false;
        try
        {
            length = stream.Length;
            return length >= 0;
        }
        catch (Exception exception) when (exception is NotSupportedException or IOException)
        {
            return false;
        }
    }
}
