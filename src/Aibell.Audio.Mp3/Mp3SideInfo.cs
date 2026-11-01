namespace Aibell.Audio.Mp3;

/// <summary>The side information of one granule and channel.</summary>
internal sealed class Mp3GranuleChannel
{
    public int Part23Length;
    public int BigValues;
    public int GlobalGain;
    public int ScalefacCompress;
    public bool WindowSwitching;
    public int BlockType;
    public bool Mixed;
    public readonly int[] TableSelect = new int[3];
    public readonly int[] SubblockGain = new int[3];
    public int Region0Count;
    public int Region1Count;
    public bool Preflag;
    public bool ScalefacScale;
    public int Count1TableSelect;

    // Scalefactors: long bands 0-21, short bands as band * 3 + window.
    public readonly int[] ScalefactorsLong = new int[23];
    public readonly int[] ScalefactorsShort = new int[39 + 3];

    // The largest value each scalefactor can have, which marks an illegal intensity position (MPEG-2 only).
    public readonly int[] LimitLong = new int[23];
    public readonly int[] LimitShort = new int[39 + 3];

    public int IntensityScale;

    public bool IsShort => WindowSwitching && BlockType == 2;
}

internal sealed class Mp3SideInfo
{
    public int MainDataBegin;
    public readonly bool[][] Scfsi = [new bool[4], new bool[4]];
    public readonly Mp3GranuleChannel[][] Granules = [[new(), new()], [new(), new()]];

    /// <summary>Parses the side information (<paramref name="offset"/> is the first byte after the header and CRC).</summary>
    public bool Parse(byte[] frame, int offset, in Mp3Header header)
    {
        Mp3BitReader reader = new(frame) { Position = offset * 8 };
        int channels = header.Channels;
        if (header.IsMpeg1)
        {
            MainDataBegin = (int)reader.Read(9);
            reader.Read(channels == 1 ? 5 : 3);
            for (int ch = 0; ch < channels; ch++)
            {
                for (int band = 0; band < 4; band++) Scfsi[ch][band] = reader.Read(1) != 0;
            }
        }
        else
        {
            MainDataBegin = (int)reader.Read(8);
            reader.Read(channels == 1 ? 1 : 2);
        }
        for (int gr = 0; gr < header.Granules; gr++)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                Mp3GranuleChannel g = Granules[gr][ch];
                g.Part23Length = (int)reader.Read(12);
                g.BigValues = (int)reader.Read(9);
                g.GlobalGain = (int)reader.Read(8);
                g.ScalefacCompress = (int)reader.Read(header.IsMpeg1 ? 4 : 9);
                g.WindowSwitching = reader.Read(1) != 0;
                if (g.WindowSwitching)
                {
                    g.BlockType = (int)reader.Read(2);
                    g.Mixed = reader.Read(1) != 0;
                    g.TableSelect[0] = (int)reader.Read(5);
                    g.TableSelect[1] = (int)reader.Read(5);
                    g.TableSelect[2] = 0;
                    for (int w = 0; w < 3; w++) g.SubblockGain[w] = (int)reader.Read(3);
                    if (g.BlockType == 0) return false;
                    // Region boundaries are implicit: the table of the short-block layout counts every band three times.
                    g.Region0Count = g.BlockType == 2 && !g.Mixed ? 8 : 7;
                    g.Region1Count = 36;
                }
                else
                {
                    g.BlockType = 0;
                    g.Mixed = false;
                    for (int region = 0; region < 3; region++) g.TableSelect[region] = (int)reader.Read(5);
                    Array.Clear(g.SubblockGain);
                    g.Region0Count = (int)reader.Read(4);
                    g.Region1Count = (int)reader.Read(3);
                }
                g.Preflag = header.IsMpeg1 && reader.Read(1) != 0;
                g.ScalefacScale = reader.Read(1) != 0;
                g.Count1TableSelect = (int)reader.Read(1);
            }
        }
        return true;
    }
}
