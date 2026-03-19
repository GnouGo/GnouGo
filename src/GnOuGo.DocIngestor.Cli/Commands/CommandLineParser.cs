namespace DocIngestor.Cli.Commands;

/// <summary>
/// Utilitaires pour parser les arguments de ligne de commande.
/// </summary>
public static class CommandLineParser
{
    public static string? GetArg(string[] args, string key)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length) return args[i + 1];
                return null;
            }
        }
        return null;
    }

    public static int GetInt(string[] args, string key, int defaultValue)
        => int.TryParse(GetArg(args, key), out var v) ? v : defaultValue;

    public static double GetDouble(string[] args, string key, double defaultValue)
        => double.TryParse(GetArg(args, key), out var v) ? v : defaultValue;

    public static bool GetBool(string[] args, string key, bool defaultValue)
        => bool.TryParse(GetArg(args, key), out var v) ? v : defaultValue;

    public static string? GetPemFromArgs(string[] args, string pathKey, string inlineKey)
    {
        var inline = GetArg(args, inlineKey);
        if (!string.IsNullOrWhiteSpace(inline))
            return inline;

        var p = GetArg(args, pathKey);
        if (!string.IsNullOrWhiteSpace(p) && File.Exists(p))
            return File.ReadAllText(p);

        return null;
    }

    public static (string cmd, string[] rest) ParseCommand(string[] args)
    {
        if (args.Length == 0) return ("help", args);
        var cmd = args[0].Trim();
        var rest = args.Skip(1).ToArray();
        return (cmd, rest);
    }
}

