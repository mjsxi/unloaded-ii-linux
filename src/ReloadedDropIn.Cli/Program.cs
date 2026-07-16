using ReloadedDropIn.Adapter.Abstractions;
using ReloadedDropIn.Adapter.GBFR;
using ReloadedDropIn.Cli;

var registeredAdapters = new IGameAdapter[]
    { new GbfrAdapter(), new ReloadedDropIn.Adapter.P5R.P5rAdapter(), new ReloadedDropIn.Adapter.FFXVI.FfxviAdapter() };

if (args.Length == 0)
    return PrintUsage();

var command = args[0];
var gameDirectory = OptionValue(args, "--game-dir") ?? Environment.CurrentDirectory;
var context = new AdapterContext
{
    GameDirectory = Path.GetFullPath(gameDirectory),
    ModsDirectory = Path.Combine(Path.GetFullPath(gameDirectory), "mods"),
    DropInDirectory = Path.Combine(Path.GetFullPath(gameDirectory), "reloaded-dropin"),
};

var commands = new Commands(registeredAdapters, context, Console.Out);

return command switch
{
    "detect" => commands.Detect(),
    "doctor" => commands.Doctor(),
    "list-mods" => commands.ListMods(),
    "validate" => commands.Validate(),
    "sync" => commands.Sync(dryRun: args.Contains("--dry-run")),
    "restore" => commands.Restore(),
    "--help" or "-h" or "help" => PrintUsage(),
    _ => UnknownCommand(command),
};

static string? OptionValue(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static int PrintUsage()
{
    Console.WriteLine(
        """
        reloaded-dropin — drag-and-drop frontend for Reloaded-II

        usage: reloaded-dropin <command> [--game-dir <path>] [options]

        commands:
          detect       identify the game in the target directory
          doctor       report environment, mods, and readiness
          list-mods    list discovered mods in mods/
          validate     run adapter installation checks
          sync         generate Reloaded configuration from mods/  (--dry-run supported)
          restore      undo all game-file changes (run with the game closed)
        """);
    return 2;
}

static int UnknownCommand(string command)
{
    Console.Error.WriteLine($"unknown command: {command} (try --help)");
    return 2;
}
