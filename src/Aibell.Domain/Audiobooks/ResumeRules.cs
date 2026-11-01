namespace Aibell.Domain.Audiobooks;

public static class ResumeRules
{
    /// <summary>Upper bound for durations taken from untrusted file metadata.</summary>
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromDays(30);

    /// <summary>Bounds an untrusted metadata duration into [0, MaximumDuration].</summary>
    public static TimeSpan BoundDuration(TimeSpan duration) =>
        duration <= TimeSpan.Zero ? TimeSpan.Zero : duration > MaximumDuration ? MaximumDuration : duration;

    /// <summary>Clamps a saved position to the file's real current duration; an unknown (zero) duration yields zero.</summary>
    public static TimeSpan Clamp(TimeSpan saved, TimeSpan actualDuration)
    {
        if (saved <= TimeSpan.Zero || actualDuration <= TimeSpan.Zero) return TimeSpan.Zero;
        return saved > actualDuration ? actualDuration : saved;
    }

    /// <summary>The position Resume starts at: the clamped saved position, or zero once the saved position reached the end.</summary>
    public static TimeSpan ResumeStart(TimeSpan saved, TimeSpan actualDuration)
    {
        TimeSpan clamped = Clamp(saved, actualDuration);
        return actualDuration > TimeSpan.Zero && clamped >= actualDuration ? TimeSpan.Zero : clamped;
    }

    /// <summary>True when the saved position is at or beyond the end of the real duration.</summary>
    public static bool IsFinished(TimeSpan saved, TimeSpan actualDuration) => actualDuration > TimeSpan.Zero && saved >= actualDuration;

    /// <summary>Position to persist when the user saves: never negative and never beyond the duration (zero when unknown).</summary>
    public static TimeSpan ForSave(TimeSpan position, TimeSpan actualDuration) => Clamp(position, actualDuration);
}
