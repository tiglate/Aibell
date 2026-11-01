namespace Aibell.Metadata.MusicBrainz;

internal static class StringSimilarity
{
    public static double Compute(string? left, string? right)
    {
        string normalizedLeft = Normalize(left);
        string normalizedRight = Normalize(right);

        if (normalizedLeft.Length == 0 && normalizedRight.Length == 0)
        {
            return 1;
        }

        if (normalizedLeft.Length == 0 || normalizedRight.Length == 0)
        {
            return 0;
        }

        int distance = LevenshteinDistance(normalizedLeft, normalizedRight);
        int maxLength = Math.Max(normalizedLeft.Length, normalizedRight.Length);
        return 1 - ((double)distance / maxLength);
    }

    private static string Normalize(string? value)
    {
        return value is null ? string.Empty : value.Trim().ToUpperInvariant();
    }

    private static int LevenshteinDistance(string left, string right)
    {
        int[] previous = new int[right.Length + 1];
        int[] current = new int[right.Length + 1];

        for (int j = 0; j <= right.Length; j++)
        {
            previous[j] = j;
        }

        for (int i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= right.Length; j++)
            {
                int cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }
}
