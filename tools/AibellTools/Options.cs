namespace AibellTools;

/// <summary>Minimal command-line parsing: <c>--flag</c>, <c>--name value</c> and positional arguments.</summary>
internal sealed class Options
{
    private readonly Dictionary<string, string?> named = new(StringComparer.Ordinal);

    public Options(IReadOnlyList<string> args, IReadOnlySet<string> flags, IReadOnlySet<string> values)
    {
        for (int index = 0; index < args.Count; index++)
        {
            string arg = args[index];
            if (flags.Contains(arg)) named[arg] = null;
            else if (values.Contains(arg))
            {
                if (index + 1 >= args.Count) throw new ToolException($"{arg} needs a value.");
                named[arg] = args[++index];
            }
            else if (arg.StartsWith("--", StringComparison.Ordinal)) throw new ToolException($"Unknown option {arg}.");
            else Positional.Add(arg);
        }
    }

    public List<string> Positional { get; } = [];

    public bool Has(string name) => named.ContainsKey(name);

    public string? Value(string name) => named.GetValueOrDefault(name);
}
