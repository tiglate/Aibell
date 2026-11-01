// This file is derived from the Opus reference implementation published in RFC 6716 (celt/*.c).
// Copyright 1994-2011 IETF Trust, Xiph.Org, Skype Limited, Octasic, Jean-Marc Valin, Timothy B. Terriberry,
// CSIRO, Gregory Maxwell, Mark Borgerding, Erik de Castro Lopo. All rights reserved.
// Redistribution and use are permitted under the BSD terms reproduced in COPYING.Opus.
namespace Aibell.Audio.Opus;

/// <summary>The one CELT mode Opus uses: 48 kHz, 960-sample frames made of up to eight 120-sample short blocks and 21 bands.</summary>
internal static class CeltMode
{
    public const int BandCount = 21;
    public const int ShortMdctSize = 120;
    public const int MaxLm = 3;
    public const int Overlap = 120;
    public const int AllocationRows = 11;

    public static readonly short[] BandEdges = CeltTables.BandEdges5ms;

    public static readonly float[] Window = BuildWindow();

    private static float[] BuildWindow()
    {
        float[] window = new float[Overlap];
        for (int i = 0; i < Overlap; i++)
        {
            double s = Math.Sin(.5 * 3.141592653 * (i + .5) / Overlap);
            window[i] = (float)Math.Sin(.5 * 3.141592653 * s * s);
        }
        return window;
    }
}
