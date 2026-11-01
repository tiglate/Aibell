namespace Aibell.Audio.Vorbis;

/// <summary>Floor type 1 (section 7): a piecewise linear spectral envelope on a dB scale, set up from the header and decoded per packet.</summary>
internal sealed class VorbisFloor1
{
    private static readonly int[] Ranges = [256, 128, 86, 64];

    private readonly int[] partitionClass;
    private readonly int[] classDimensions;
    private readonly int[] classSubclasses;
    private readonly int[] classMasterBooks;
    private readonly int[][] subclassBooks;
    private readonly int multiplier;
    private readonly int range;
    private readonly int yBits;
    private readonly int[] xList;
    private readonly int[] sortedIndex;
    private readonly int[] lowNeighbor;
    private readonly int[] highNeighbor;
    private readonly int[] finalY;
    private readonly bool[] step2;

    private VorbisFloor1(int[] partitionClass, int[] classDimensions, int[] classSubclasses, int[] classMasterBooks, int[][] subclassBooks, int multiplier, int[] xList)
    {
        this.partitionClass = partitionClass;
        this.classDimensions = classDimensions;
        this.classSubclasses = classSubclasses;
        this.classMasterBooks = classMasterBooks;
        this.subclassBooks = subclassBooks;
        this.multiplier = multiplier;
        this.xList = xList;
        range = Ranges[multiplier - 1];
        yBits = VorbisCodebook.Ilog(range - 1);
        Values = xList.Length;
        finalY = new int[Values];
        step2 = new bool[Values];
        sortedIndex = [.. Enumerable.Range(0, Values).OrderBy(i => xList[i])];
        lowNeighbor = new int[Values];
        highNeighbor = new int[Values];
        for (int i = 2; i < Values; i++)
        {
            int low = 0, high = 1;
            int lowX = -1, highX = int.MaxValue;
            for (int n = 0; n < i; n++)
            {
                if (xList[n] < xList[i] && xList[n] > lowX)
                {
                    lowX = xList[n];
                    low = n;
                }
                if (xList[n] > xList[i] && xList[n] < highX)
                {
                    highX = xList[n];
                    high = n;
                }
            }
            lowNeighbor[i] = low;
            highNeighbor[i] = high;
        }
        Y = new int[Values];
    }

    public int Values { get; }

    /// <summary>The values read from the last packet (floor1_Y).</summary>
    public int[] Y { get; }

    public static VorbisFloor1 Read(ref VorbisBitReader reader, int codebookCount)
    {
        int partitions = (int)reader.Read(5);
        int[] partitionClass = new int[partitions];
        int maximumClass = -1;
        for (int i = 0; i < partitions; i++)
        {
            partitionClass[i] = (int)reader.Read(4);
            maximumClass = Math.Max(maximumClass, partitionClass[i]);
        }
        int classes = maximumClass + 1;
        int[] dimensions = new int[classes], subclasses = new int[classes], masters = new int[classes];
        int[][] books = new int[classes][];
        for (int i = 0; i < classes; i++)
        {
            dimensions[i] = (int)reader.Read(3) + 1;
            subclasses[i] = (int)reader.Read(2);
            if (subclasses[i] != 0)
            {
                masters[i] = (int)reader.Read(8);
                if (masters[i] >= codebookCount) throw new InvalidDataException("A Vorbis floor refers to a missing codebook.");
            }
            books[i] = new int[1 << subclasses[i]];
            for (int j = 0; j < books[i].Length; j++)
            {
                books[i][j] = (int)reader.Read(8) - 1;
                if (books[i][j] >= codebookCount) throw new InvalidDataException("A Vorbis floor refers to a missing codebook.");
            }
        }
        int multiplier = (int)reader.Read(2) + 1;
        int rangeBits = (int)reader.Read(4);
        List<int> x = [0, 1 << rangeBits];
        foreach (int cls in partitionClass)
        {
            for (int j = 0; j < dimensions[cls]; j++) x.Add((int)reader.Read(rangeBits));
        }
        if (reader.EndOfPacket) throw new InvalidDataException("A Vorbis floor ends early.");
        if (x.Count > 65 || x.Distinct().Count() != x.Count) throw new InvalidDataException("A Vorbis floor has an invalid X list.");
        return new VorbisFloor1(partitionClass, dimensions, subclasses, masters, books, multiplier, [.. x]);
    }

    /// <summary>Reads the floor of one channel. False when the channel is unused in this packet (including the end of the packet).</summary>
    public bool Decode(ref VorbisBitReader reader, VorbisCodebook[] codebooks)
    {
        if (reader.Read(1) == 0) return false;
        Y[0] = (int)reader.Read(yBits);
        Y[1] = (int)reader.Read(yBits);
        int offset = 2;
        for (int i = 0; i < partitionClass.Length; i++)
        {
            int cls = partitionClass[i];
            int dimensions = classDimensions[cls];
            int cbits = classSubclasses[cls];
            int csub = (1 << cbits) - 1;
            int cval = 0;
            if (cbits > 0)
            {
                cval = codebooks[classMasterBooks[cls]].DecodeScalar(ref reader);
                if (cval < 0) return false;
            }
            for (int j = 0; j < dimensions; j++)
            {
                int book = subclassBooks[cls][cval & csub];
                cval >>= cbits;
                if (book >= 0)
                {
                    int value = codebooks[book].DecodeScalar(ref reader);
                    if (value < 0) return false;
                    Y[offset + j] = value;
                }
                else Y[offset + j] = 0;
            }
            offset += dimensions;
        }
        return !reader.EndOfPacket;
    }

    /// <summary>Turns the decoded values into a linear-amplitude curve of <paramref name="n"/> points (steps 1 and 2 of section 7.2.4).</summary>
    public void Synthesize(Span<float> curve, int n)
    {
        // Step 1: amplitude value synthesis.
        step2[0] = step2[1] = true;
        finalY[0] = Y[0];
        finalY[1] = Y[1];
        for (int i = 2; i < Values; i++)
        {
            int low = lowNeighbor[i], high = highNeighbor[i];
            int predicted = RenderPoint(xList[low], finalY[low], xList[high], finalY[high], xList[i]);
            int value = Y[i];
            int highRoom = range - predicted;
            int lowRoom = predicted;
            int room = highRoom < lowRoom ? highRoom * 2 : lowRoom * 2;
            if (value != 0)
            {
                step2[low] = step2[high] = step2[i] = true;
                if (value >= room) finalY[i] = highRoom > lowRoom ? value - lowRoom + predicted : predicted - value + highRoom - 1;
                else finalY[i] = (value & 1) != 0 ? predicted - ((value + 1) >> 1) : predicted + (value >> 1);
            }
            else
            {
                step2[i] = false;
                finalY[i] = predicted;
            }
            finalY[i] = Math.Clamp(finalY[i], 0, range - 1);
        }

        // Step 2: curve synthesis in ascending X order.
        int hx = 0, lx = 0;
        int ly = finalY[sortedIndex[0]] * multiplier;
        int hy = ly;
        for (int k = 1; k < Values; k++)
        {
            int i = sortedIndex[k];
            if (!step2[i]) continue;
            hy = finalY[i] * multiplier;
            hx = xList[i];
            RenderLine(lx, ly, hx, hy, curve, n);
            lx = hx;
            ly = hy;
        }
        if (hx < n) RenderLine(hx, hy, n, hy, curve, n);
    }

    private static int RenderPoint(int x0, int y0, int x1, int y1, int x)
    {
        int dy = y1 - y0;
        int adx = x1 - x0;
        int ady = Math.Abs(dy);
        int error = ady * (x - x0);
        int offset = error / adx;
        return dy < 0 ? y0 - offset : y0 + offset;
    }

    private static void RenderLine(int x0, int y0, int x1, int y1, Span<float> curve, int n)
    {
        int dy = y1 - y0;
        int adx = x1 - x0;
        int ady = Math.Abs(dy);
        int baseStep = dy / adx;
        int sy = dy < 0 ? baseStep - 1 : baseStep + 1;
        ady -= Math.Abs(baseStep) * adx;
        int y = y0, error = 0;
        float[] table = Floor1Table.InverseDb;
        if (x0 < n) curve[x0] = table[Math.Clamp(y, 0, 255)];
        int end = Math.Min(x1, n);
        for (int x = x0 + 1; x < end; x++)
        {
            error += ady;
            if (error >= adx)
            {
                error -= adx;
                y += sy;
            }
            else y += baseStep;
            curve[x] = table[Math.Clamp(y, 0, 255)];
        }
    }
}
