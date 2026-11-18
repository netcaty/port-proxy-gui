namespace PortProxyManager;

public sealed class CliOptions
{
    public string? DataDir { get; private set; }
    public string? OutFile { get; private set; }
    public bool Tray { get; private set; }
    public bool Dump { get; private set; }
    public bool Hints { get; private set; }
    public bool SelfTest { get; private set; }
    public bool NoElevate { get; private set; }
    public bool Help { get; private set; }
    public bool Json { get; private set; }

    public List<string> Unknown { get; } = new();

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i].Trim();
            if (arg.Length == 0) continue;

            var (name, inlineValue) = SplitArg(arg);

            switch (name)
            {
                case "--data-dir":
                case "-d":
                    options.DataDir = inlineValue ?? TakeValue(args, ref i);
                    break;

                case "--out":
                case "-o":
                    options.OutFile = inlineValue ?? TakeValue(args, ref i);
                    break;

                case "--tray":
                    options.Tray = true;
                    break;

                case "--dump":
                    options.Dump = true;
                    break;

                case "--hints":
                    options.Hints = true;
                    break;

                case "--selftest":
                    options.SelfTest = true;
                    break;

                case "--no-elevate":
                    options.NoElevate = true;
                    break;

                case "--json":
                    options.Json = true;
                    break;

                case "-h":
                case "--help":
                case "/?":
                    options.Help = true;
                    break;

                default:
                    options.Unknown.Add(arg);
                    break;
            }
        }

        return options;
    }

    private static (string Name, string? Value) SplitArg(string arg)
    {
        var eq = arg.IndexOf('=');
        if (eq <= 0) return (arg, null);
        return (arg[..eq], arg[(eq + 1)..]);
    }

    private static string? TakeValue(string[] args, ref int index)
    {
        if (index + 1 >= args.Length) return null;
        var next = args[index + 1];
        if (next.StartsWith("--", StringComparison.Ordinal)) return null;
        index++;
        return next;
    }
}
