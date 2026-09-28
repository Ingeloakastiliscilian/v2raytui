namespace V2RayTui.Cli;

/// <summary>Minimal argument parser: positionals, --flags, --key value / --key=value, short aliases.</summary>
public sealed class ArgList
{
    private static readonly Dictionary<string, string> _aliases = new()
    {
        ["-s"] = "--sub",
        ["-m"] = "--mode",
        ["-n"] = "--top",
        ["-h"] = "--help",
        ["-j"] = "--json",
        ["-l"] = "--log",
    };

    private static readonly HashSet<string> _valueOptions = ["--sub", "--mode", "--top", "--log", "--lang", "--name", "--limit", "--interval", "--data", "--out"];

    public List<string> Positional { get; } = [];
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

    public ArgList(IEnumerable<string> args)
    {
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var a = list[i];
            if (_aliases.TryGetValue(a, out var full))
            {
                a = full;
            }
            if (a.StartsWith("--") && a.Length > 2)
            {
                var eq = a.IndexOf('=');
                if (eq > 0)
                {
                    _options[a[..eq]] = a[(eq + 1)..];
                }
                else if (_valueOptions.Contains(a) && i + 1 < list.Count)
                {
                    _options[a] = list[++i];
                }
                else
                {
                    _options[a] = null;
                }
            }
            else
            {
                Positional.Add(a);
            }
        }
    }

    public bool Has(string name) => _options.ContainsKey(name);

    public string? Get(string name) => _options.GetValueOrDefault(name);

    public int GetInt(string name, int def) => int.TryParse(Get(name), out var v) ? v : def;

    public string? Pos(int i) => i < Positional.Count ? Positional[i] : null;
}
