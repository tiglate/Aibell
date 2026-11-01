namespace Aibell.Audio.Vorbis;

/// <summary>Residue types 0, 1 and 2 (section 8): the fine spectral detail, coded in partitions by cascaded vector quantization.</summary>
internal sealed class VorbisResidue
{
    private readonly int type;
    private readonly int begin;
    private readonly int end;
    private readonly int partitionSize;
    private readonly int classifications;
    private readonly int classBook;
    private readonly int[][] books;
    private readonly int passes;
    private int[] classBuffer = [];
    private readonly float[][] flatBundle = new float[1][];
    private readonly bool[] noSkip = new bool[1];
    private float[] flat = [];

    private VorbisResidue(int type, int begin, int end, int partitionSize, int classifications, int classBook, int[][] books)
    {
        this.type = type;
        this.begin = begin;
        this.end = end;
        this.partitionSize = partitionSize;
        this.classifications = classifications;
        this.classBook = classBook;
        this.books = books;
        int used = 0;
        foreach (int[] row in books)
        {
            for (int j = 0; j < row.Length; j++)
            {
                if (row[j] >= 0) used = Math.Max(used, j + 1);
            }
        }
        passes = used;
    }

    public static VorbisResidue Read(ref VorbisBitReader reader, int type, VorbisCodebook[] codebooks)
    {
        int begin = (int)reader.Read(24);
        int end = (int)reader.Read(24);
        int partitionSize = (int)reader.Read(24) + 1;
        int classifications = (int)reader.Read(6) + 1;
        int classBook = (int)reader.Read(8);
        if (classBook >= codebooks.Length) throw new InvalidDataException("A Vorbis residue refers to a missing codebook.");
        int[] cascade = new int[classifications];
        for (int i = 0; i < classifications; i++)
        {
            int high = 0;
            int low = (int)reader.Read(3);
            if (reader.Read(1) == 1) high = (int)reader.Read(5);
            cascade[i] = high * 8 + low;
        }
        int[][] books = new int[classifications][];
        for (int i = 0; i < classifications; i++)
        {
            books[i] = new int[8];
            for (int j = 0; j < 8; j++)
            {
                if ((cascade[i] >> j & 1) == 0)
                {
                    books[i][j] = -1;
                    continue;
                }
                int book = (int)reader.Read(8);
                if (book >= codebooks.Length || !codebooks[book].HasVectors || partitionSize % codebooks[book].Dimensions != 0)
                    throw new InvalidDataException("A Vorbis residue uses an unsuitable codebook.");
                books[i][j] = book;
            }
        }
        if (reader.EndOfPacket) throw new InvalidDataException("A Vorbis residue ends early.");
        VorbisCodebook classes = codebooks[classBook];
        double combinations = Math.Pow(classifications, classes.Dimensions);
        if (combinations > classes.Entries) throw new InvalidDataException("A Vorbis residue's class book is too small.");
        return new VorbisResidue(type, begin, end, partitionSize, classifications, classBook, books);
    }

    /// <summary>
    /// Decodes the vectors of one submap. <paramref name="vectors"/> holds one zeroed array per channel of the bundle; the end of the packet leaves
    /// whatever was decoded so far.
    /// </summary>
    public void Decode(ref VorbisBitReader reader, VorbisCodebook[] codebooks, int half, float[][] vectors, ReadOnlySpan<bool> doNotDecode)
    {
        int count = vectors.Length;
        if (type == 2)
        {
            bool any = false;
            foreach (bool skip in doNotDecode) any |= !skip;
            if (!any) return;
            int total = half * count;
            if (flat.Length < total)
            {
                flat = new float[total];
                flatBundle[0] = flat;
            }
            Array.Clear(flat, 0, total);
            DecodeBundle(ref reader, codebooks, total, flatBundle, noSkip);
            for (int j = 0; j < count; j++)
            {
                float[] target = vectors[j];
                for (int i = 0; i < half; i++) target[i] = flat[i * count + j];
            }
            return;
        }
        DecodeBundle(ref reader, codebooks, half, vectors, doNotDecode);
    }

    private void DecodeBundle(ref VorbisBitReader reader, VorbisCodebook[] codebooks, int actualSize, float[][] vectors, ReadOnlySpan<bool> doNotDecode)
    {
        int count = vectors.Length;
        int limitBegin = Math.Min(begin, actualSize);
        int limitEnd = Math.Min(end, actualSize);
        int toRead = limitEnd - limitBegin;
        if (toRead <= 0) return;
        VorbisCodebook classBookInstance = codebooks[classBook];
        int classWords = classBookInstance.Dimensions;
        int partitionsToRead = toRead / partitionSize;
        int stride = partitionsToRead + classWords;
        if (classBuffer.Length < count * stride) classBuffer = new int[count * stride];
        Array.Clear(classBuffer, 0, count * stride);

        for (int pass = 0; pass < Math.Max(passes, 1); pass++)
        {
            int partition = 0;
            while (partition < partitionsToRead)
            {
                if (pass == 0)
                {
                    for (int j = 0; j < count; j++)
                    {
                        if (doNotDecode[j]) continue;
                        int temp = classBookInstance.DecodeScalar(ref reader);
                        if (temp < 0) return;
                        for (int i = classWords - 1; i >= 0; i--)
                        {
                            classBuffer[j * stride + i + partition] = temp % classifications;
                            temp /= classifications;
                        }
                    }
                }
                for (int i = 0; i < classWords && partition < partitionsToRead; i++)
                {
                    for (int j = 0; j < count; j++)
                    {
                        if (doNotDecode[j]) continue;
                        int book = books[classBuffer[j * stride + partition]][pass];
                        if (book < 0) continue;
                        if (!DecodePartition(ref reader, codebooks[book], vectors[j], limitBegin + partition * partitionSize)) return;
                    }
                    partition++;
                }
            }
        }
    }

    private bool DecodePartition(ref VorbisBitReader reader, VorbisCodebook book, float[] target, int offset)
    {
        int dimensions = book.Dimensions;
        int steps = partitionSize / dimensions;
        Span<float> destination = target.AsSpan(offset, partitionSize);
        if (type == 0)
        {
            for (int i = 0; i < steps; i++)
            {
                ReadOnlySpan<float> entry = book.DecodeVector(ref reader);
                if (entry.IsEmpty) return false;
                for (int j = 0; j < dimensions; j++) destination[i + j * steps] += entry[j];
            }
            return true;
        }
        int at = 0;
        for (int i = 0; i < steps; i++)
        {
            ReadOnlySpan<float> entry = book.DecodeVector(ref reader);
            if (entry.IsEmpty) return false;
            for (int j = 0; j < dimensions; j++) destination[at + j] += entry[j];
            at += dimensions;
        }
        return true;
    }
}
