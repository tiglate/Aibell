using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Aibell.Playback.Core;

/// <summary>
/// Windows Media Foundation decoding (AAC, M4A, WMA and anything else Windows has a codec for) through its Source Reader, which
/// also resamples to the output rate. The COM objects are driven through their vtables, like the rest of the Windows interop here.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class MediaFoundationSource : IPcmSource
{
    private const uint MfVersion = 0x00020070;
    private const uint FirstAudioStream = 0xFFFFFFFD;
    private const uint AllStreams = 0xFFFFFFFE;
    private const uint EndOfStream = 0x2;
    private const ushort VtI8 = 20;
    private static readonly Lock StartupLock = new();
    private static int startups;

    private readonly uint sampleRate;
    private nint reader;
    private float[] pending = [];
    private int pendingOffset;
    private int pendingFrames;
    private bool ended;

    private MediaFoundationSource(nint reader, uint sampleRate)
    {
        this.reader = reader;
        this.sampleRate = sampleRate;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public long Value;
    }

    [LibraryImport("mfplat.dll")] private static partial int MFStartup(uint version, uint flags);
    [LibraryImport("mfplat.dll")] private static partial int MFShutdown();
    [LibraryImport("mfplat.dll")] private static partial int MFCreateMediaType(out nint type);
    [LibraryImport("mfreadwrite.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MFCreateSourceReaderFromURL(string url, nint attributes, out nint reader);

    /// <summary>Opens a file path or an http(s) URL. Throws when Windows has no decoder for it.</summary>
    public static MediaFoundationSource Open(string location, uint sampleRate)
    {
        lock (StartupLock)
        {
            if (startups++ == 0) Check(MFStartup(MfVersion, 0), "start Media Foundation");
        }
        nint reader = 0;
        try
        {
            Check(MFCreateSourceReaderFromURL(location, 0, out reader), "open the file with Windows");
            ((delegate* unmanaged[Stdcall]<nint, uint, int, int>)Slot(reader, 4))(reader, AllStreams, 0);
            Check(((delegate* unmanaged[Stdcall]<nint, uint, int, int>)Slot(reader, 4))(reader, FirstAudioStream, 1), "select the audio stream");
            ConfigureOutput(reader, sampleRate);
            return new MediaFoundationSource(reader, sampleRate);
        }
        catch
        {
            if (reader != 0) Release(reader);
            Shutdown();
            throw;
        }
    }

    public int Read(Span<float> destination, int frames)
    {
        int produced = 0;
        while (produced < frames)
        {
            if (pendingFrames == 0 && !Refill()) break;
            int take = Math.Min(frames - produced, pendingFrames);
            pending.AsSpan(pendingOffset * AudioChannels.Channels, take * AudioChannels.Channels)
                .CopyTo(destination[(produced * AudioChannels.Channels)..]);
            pendingOffset += take;
            pendingFrames -= take;
            produced += take;
        }
        return produced;
    }

    public bool Seek(long frame)
    {
        ObjectDisposedException.ThrowIf(reader == 0, this);
        PropVariant position = new() { Type = VtI8, Value = frame * 10_000_000L / sampleRate };
        Guid timeFormat = Guid.Empty;
        int result = ((delegate* unmanaged[Stdcall]<nint, Guid*, PropVariant*, int>)Slot(reader, 8))(reader, &timeFormat, &position);
        pendingFrames = 0;
        pendingOffset = 0;
        ended = false;
        return result >= 0;
    }

    public void Dispose()
    {
        nint native = Interlocked.Exchange(ref reader, 0);
        if (native == 0) return;
        Release(native);
        Shutdown();
    }

    private bool Refill()
    {
        if (ended) return false;
        while (true)
        {
            uint streamFlags;
            long timestamp;
            nint sample;
            Check(((delegate* unmanaged[Stdcall]<nint, uint, uint, uint*, uint*, long*, nint*, int>)Slot(reader, 9))(
                reader, FirstAudioStream, 0, null, &streamFlags, &timestamp, &sample), "decode audio");
            if ((streamFlags & EndOfStream) != 0)
            {
                if (sample != 0) Release(sample);
                ended = true;
                return false;
            }
            if (sample == 0) continue;
            try
            {
                nint buffer;
                Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(sample, 41))(sample, &buffer), "read decoded audio");
                try
                {
                    byte* data;
                    uint current;
                    Check(((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)Slot(buffer, 3))(buffer, &data, null, &current), "lock decoded audio");
                    try
                    {
                        pendingFrames = (int)(current / (sizeof(float) * AudioChannels.Channels));
                        if (pending.Length < pendingFrames * AudioChannels.Channels) pending = new float[pendingFrames * AudioChannels.Channels];
                        new ReadOnlySpan<float>(data, pendingFrames * AudioChannels.Channels).CopyTo(pending);
                        pendingOffset = 0;
                    }
                    finally
                    {
                        ((delegate* unmanaged[Stdcall]<nint, int>)Slot(buffer, 4))(buffer);
                    }
                }
                finally
                {
                    Release(buffer);
                }
            }
            finally
            {
                Release(sample);
            }
            if (pendingFrames > 0) return true;
        }
    }

    private static void ConfigureOutput(nint reader, uint sampleRate)
    {
        Check(MFCreateMediaType(out nint type), "describe the output format");
        try
        {
            SetGuid(type, MajorType, AudioType);
            SetGuid(type, Subtype, FloatSubtype);
            SetUInt32(type, Channels, AudioChannels.Channels);
            SetUInt32(type, SamplesPerSecond, sampleRate);
            SetUInt32(type, BitsPerSample, 32);
            SetUInt32(type, BlockAlignment, sizeof(float) * AudioChannels.Channels);
            SetUInt32(type, AverageBytesPerSecond, sampleRate * sizeof(float) * AudioChannels.Channels);
            SetUInt32(type, AllSamplesIndependent, 1);
            Check(((delegate* unmanaged[Stdcall]<nint, uint, uint*, nint, int>)Slot(reader, 7))(reader, FirstAudioStream, null, type),
                "convert the audio for playback");
        }
        finally
        {
            Release(type);
        }
    }

    // IMFAttributes: SetGUID is vtable slot 24 and SetUINT32 slot 21.
    private static void SetGuid(nint attributes, Guid key, Guid value) =>
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(attributes, 24))(attributes, &key, &value), "describe the output format");

    private static void SetUInt32(nint attributes, Guid key, uint value) =>
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Slot(attributes, 21))(attributes, &key, value), "describe the output format");

    private static nint Slot(nint comObject, int index) => (*(nint**)comObject)[index];

    private static void Release(nint comObject) => ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(comObject, 2))(comObject);

    private static void Shutdown()
    {
        lock (StartupLock)
        {
            if (--startups == 0) _ = MFShutdown();
        }
    }

    private static void Check(int result, string action)
    {
        if (result < 0) throw new InvalidOperationException($"Windows could not {action} (0x{result:X8}).");
    }

    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid AudioType = new("73647561-0000-0010-8000-00aa00389b71");
    private static readonly Guid FloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid Channels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid SamplesPerSecond = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid BlockAlignment = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    private static readonly Guid AverageBytesPerSecond = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    private static readonly Guid BitsPerSample = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    private static readonly Guid AllSamplesIndependent = new("c9173739-5e56-461c-b713-46fb995cb95f");
}
