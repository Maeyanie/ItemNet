namespace ItemNet.Cli;

public sealed class CliOptions
{
    public string Command { get; private init; } = "";
    public List<string> Positional { get; } = [];
    private readonly Dictionary<string, string> _named = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions { Command = args.Length > 0 ? args[0] : "" };
        for (var i = 1; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                var key = a[2..];
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    o._named[key] = args[++i];
                else
                    o._flags.Add(key);
            }
            else o.Positional.Add(a);
        }
        return o;
    }

    public string? Get(string key) => _named.GetValueOrDefault(key);
    public string Get(string key, string fallback) => _named.GetValueOrDefault(key) ?? fallback;
    public bool Has(string flag) => _flags.Contains(flag) || _named.ContainsKey(flag);
}
