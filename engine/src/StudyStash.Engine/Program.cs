using StudyStash.Core;

// The C# engine, stage 1 of the port: only checks that it reads and writes this computer's files the way the
// Python engine does. Nothing here changes a file.
return args.FirstOrDefault() switch
{
    "config-check" => ConfigCheck(args.Skip(1).ToArray()),
    "version" => Print(typeof(Configs).Assembly.GetName().Version?.ToString() ?? "0"),
    _ => Print("usage: studystash config-check [--home DIR] | version", 2),
};

static int Print(string text, int code = 0)
{
    (code == 0 ? Console.Out : Console.Error).WriteLine(text);
    return code;
}

// Reads config.toml and client.toml and writes them back in memory: "same" means the C# engine would leave them
// byte for byte as they are. Only line numbers are shown, never contents: these files hold the library password.
static int ConfigCheck(string[] args)
{
    int at = Array.IndexOf(args, "--home");
    string home = at >= 0 && at + 1 < args.Length ? Py.ExpandUser(args[at + 1]) : Configs.DefaultHome;
    int differ = 0;
    foreach (var (name, dump) in new (string, Func<string>)[]
             {
                 ("config.toml", () => Configs.Dump(Configs.Load(home))),
                 ("client.toml", () => Configs.DumpClient(Configs.LoadClient(home))),
             })
    {
        string path = Path.Combine(home, name);
        if (!File.Exists(path))
        {
            Console.WriteLine($"{name}: not on this computer");
            continue;
        }
        var before = Py.SplitLines(Py.ReadText(path));
        var after = Py.SplitLines(dump());
        var lines = Enumerable.Range(0, Math.Max(before.Count, after.Count))
            .Where(i => i >= before.Count || i >= after.Count || before[i] != after[i]).Select(i => i + 1).ToList();
        Console.WriteLine(lines.Count == 0 ? $"{name}: same" : $"{name}: differs on line {string.Join(", ", lines)}");
        if (lines.Count > 0) differ++;
    }
    return differ == 0 ? 0 : 1;
}
