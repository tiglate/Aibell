using System.Globalization;

namespace Aibell.Domain.Playlists;

public enum SmartPlaylistField { Title, Artist, Album, Genre, Rating, Year, PlayCount, SkipCount }
public enum SmartPlaylistOperator { Contains, Equals, NotEquals, GreaterOrEqual, LessOrEqual }
public enum SmartPlaylistMatch { All, Any }

public sealed record SmartPlaylistRule(SmartPlaylistField Field, SmartPlaylistOperator Operator, string Value)
{
    public bool IsNumeric => Field is SmartPlaylistField.Rating or SmartPlaylistField.Year
        or SmartPlaylistField.PlayCount or SmartPlaylistField.SkipCount;

    public static IReadOnlyList<SmartPlaylistOperator> OperatorsFor(SmartPlaylistField field) =>
        field is SmartPlaylistField.Rating or SmartPlaylistField.Year or SmartPlaylistField.PlayCount or SmartPlaylistField.SkipCount
            ? [SmartPlaylistOperator.Equals, SmartPlaylistOperator.NotEquals, SmartPlaylistOperator.GreaterOrEqual, SmartPlaylistOperator.LessOrEqual]
            : [SmartPlaylistOperator.Contains, SmartPlaylistOperator.Equals, SmartPlaylistOperator.NotEquals];

    public void Validate()
    {
        if (!Enum.IsDefined(Field) || !OperatorsFor(Field).Contains(Operator))
            throw new ArgumentException("Choose a supported field and comparison.");
        ArgumentNullException.ThrowIfNull(Value);
        if (IsNumeric)
        {
            int maximum = Field == SmartPlaylistField.Rating ? 5 : Field == SmartPlaylistField.Year ? 9999 : int.MaxValue;
            if (!int.TryParse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < 0 || value > maximum)
                throw new ArgumentException(Field == SmartPlaylistField.Rating
                    ? "Ratings must be a whole number from 0 through 5."
                    : Field == SmartPlaylistField.Year ? "Years must be a whole number from 0 through 9999."
                    : "Play and skip counts must be non-negative whole numbers.");
        }
        else if (Operator == SmartPlaylistOperator.Contains && string.IsNullOrWhiteSpace(Value))
        {
            throw new ArgumentException("Enter text for a contains rule.");
        }
    }
}

public sealed record SmartPlaylistDefinition(SmartPlaylistMatch Match, IReadOnlyList<SmartPlaylistRule> Rules)
{
    // An unreadable persisted definition stays identifiable and editable, but matches nothing.
    public static SmartPlaylistDefinition Unavailable { get; } = new(SmartPlaylistMatch.All, []);

    public void Validate()
    {
        if (!Enum.IsDefined(Match)) throw new ArgumentException("Choose whether all or any rules must match.");
        if (Rules is null || Rules.Count is < 1 or > 20) throw new ArgumentException("Use between 1 and 20 rules.");
        foreach (SmartPlaylistRule rule in Rules)
        {
            if (rule is null) throw new ArgumentException("Each rule must contain a field, comparison, and value.");
            rule.Validate();
        }
    }
}
