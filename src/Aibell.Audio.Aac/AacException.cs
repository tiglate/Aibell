namespace Aibell.Audio.Aac;

/// <summary>The data is not valid AAC (or is damaged). Decoders skip the frame; nothing here is a programming error.</summary>
public class AacException(string message) : Exception(message)
{
}

/// <summary>The stream is valid but uses a feature this decoder does not implement (for example SBR or a data-partitioned profile).</summary>
public sealed class AacUnsupportedException(string message) : AacException(message)
{
}
