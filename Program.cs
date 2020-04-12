using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using sttz.CLI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace sttz.expresso
{

/// <summary>
/// Command line interface for <see cref="ExpressVPNClient"/>.
/// </summary>
class ExpressoCLI
{
    /// <summary>
    /// The selected action.
    /// </summary>
    public string action;

    // -------- Options --------

    /// <summary>
    /// Wether to show the help.
    /// </summary>
    public bool help;
    /// <summary>
    /// Wether to print the program version.
    /// </summary>
    public bool version;
    /// <summary>
    /// Verbosity of the output (0 = default, 1 = verbose, 3 = extra verbose)
    /// </summary>
    public int verbose;
    /// <summary>
    /// Only log errors.
    /// </summary>
    public bool quiet;
    /// <summary>
    /// Operation timeout (ms).
    /// </summary>
    public int timeout = 10000;

    // -------- Connect --------

    /// <summary>
    /// The VPN location to connect to.
    /// </summary>
    public string location;

    // -------- Alfred --------

    /// <summary>
    /// Wether to list all locations for the Alfred workflow.
    /// </summary>
    public bool alfredLocations;

    // -------- CLI --------

    /// <summary>
    /// Name of program used in output.
    /// </summary>
    public const string PROGRAM_NAME = "expresso";

    public static ILoggerFactory LoggerFactory { get; set; } 
    public static ILogger Logger;
    public static bool enableColors;

    /// <summary>
    /// The definition of the program's arguments.
    /// </summary>
    public static Arguments<ExpressoCLI> ArgumentsDefinition {
        get {
            if (_arguments != null) return _arguments;

            _arguments = new Arguments<ExpressoCLI>()
                .Action(null, (t, a) => t.action = a)

                .Option((ExpressoCLI t, bool v) => t.help = v, "h", "?", "help")
                    .Description("Show this help")
                .Option((ExpressoCLI t, bool v) => t.version = v, "version")
                    .Description("Print the version of this program")
                .Option((ExpressoCLI t, bool v) => t.verbose++, "v", "verbose").Repeatable()
                    .Description("Increase verbosity of output, can be repeated")
                .Option((ExpressoCLI t, bool v) => t.quiet = v, "q", "quiet")
                    .Description("Only output necessary information and errors")
                .Option((ExpressoCLI t, string v) => t.timeout = int.Parse(v), "t", "timeout")
                    .Description("Override the default connect/disconnect timeout (in milliseconds)")
                
                .Action("locations", (t, a) => t.action = a)
                    .Description("List all available VPN locations")

                .Action("connect", (t, a) => t.action = a)
                    .Description("Connect to a VPN location")
                .Option((ExpressoCLI t , string a) => t.location = a, 0)
                    .ArgumentName("<location>")
                    .Description("Location to connect to, either location id, country or keyword")

                .Action("disconnect", (t, a) => t.action = a)
                    .Description("Disconnect from the current VPN location")

                .Action("alfred", (t, a) => t.action = a)
                    .Description("Output the main options for the Alfred workflow")
                .Option((ExpressoCLI t, bool a) => t.alfredLocations = a, "locations")
                    .Description("Output the locations for the Alfred workflow")

                .Action("repl", (t, a) => t.action = a)
                    .Description("Interactively communicate with the helper")
                
                ;

            return _arguments;
        }
    }
    static Arguments<ExpressoCLI> _arguments;

    /// <summary>
    /// Main entry method.
    /// </summary>
    public static async Task<int> Main(string[] args)
    {
        var cli = new ExpressoCLI();
        try {
            ArgumentsDefinition.Parse(cli, args);

            if (cli.help) {
                cli.PrintHelp();
                return 0;
            } else if (cli.version) {
                cli.PrintVersion();
                return 0;
            }

            await cli.Setup();

            switch (cli.action) {
                case "locations":
                    await cli.Locations();
                    break;
                case "connect":
                    await cli.Connect();
                    break;
                case "disconnect":
                    await cli.Disconnect();
                    break;
                case "repl":
                    await cli.Repl();
                    break;
                case "alfred":
                    await cli.Alfred();
                    break;
                default:
                    throw new Exception("Unknown action: " + cli.action);
            }
            return 0;

        } catch (Exception e) {
            Arguments<ExpressoCLI>.WriteException(e, args, cli.verbose > 0, enableColors);
            return 1;
        }
    }

    /// <summary>
    /// Print the help for this program.
    /// </summary>
    public void PrintHelp()
    {
        PrintVersion();
        Console.WriteLine();
        Console.WriteLine(ArgumentsDefinition.Help(PROGRAM_NAME, null, null));
    }

    /// <summary>
    /// Return the version of this program.
    /// </summary>
    public string GetVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion;
    }

    /// <summary>
    /// Print the program name and version to the console.
    /// </summary>
    public void PrintVersion()
    {
        Console.WriteLine($"{PROGRAM_NAME} v{GetVersion()}");
    }

    // -------- Implementation --------

    const string ExpressVPNHelperPath = "/Applications/ExpressVPN.app/Contents/MacOS/expressvpn-browser-helper";
    const string DummyManifest = "com.expressvpn.helper.json";
    const string ExtensionId = "firefox-addon@expressvpn.com";

    ExpressVPNClient client;
    string input;

    async Task Setup()
    {
        enableColors = Environment.GetEnvironmentVariable("CLICOLORS") != "0";

        var level = LogLevel.Warning;
        if (quiet) {
            level = LogLevel.Error;
        } else if (verbose >= 3) {
            level = LogLevel.Trace;
        } else if (verbose == 2) {
            level = LogLevel.Debug;
        } else if (verbose == 1) {
            level = LogLevel.Information;
        }

        LoggerFactory = new LoggerFactory()
            .AddNiceConsole(level, false);
        Logger = LoggerFactory.CreateLogger<ExpressoCLI>();

        Logger.LogInformation($"{PROGRAM_NAME} v{GetVersion()}");
        if (level != LogLevel.Warning) Logger.LogInformation($"Log level set to {level}");

        client = new ExpressVPNClient(LoggerFactory.CreateLogger<ExpressVPNClient>());
        await client.WaitForConnection();
        Logger.LogInformation($"Connected to ExpressVPN version {client.AppVersion}");

        await client.UpdateStatus();
    }

    async Task Locations()
    {
        await client.GetLocations();

        string lastRegion = null;
        string lastCountry = null;
        foreach (var loc in client.Locations
                .OrderBy(l => l.region)
                .ThenBy(l => l.country)
                .ThenBy(l => l.name)
        ) {
            if (lastRegion != loc.region) {
                Console.WriteLine("");
                Console.WriteLine($"--- {loc.region} ---");
                lastRegion = loc.region;
            }
            if (lastCountry != loc.country) {
                Console.WriteLine("");
                Console.WriteLine($"{loc.country} ({loc.country_code})");
                lastCountry = loc.country;
            }
            Console.WriteLine($"- {loc.name} ({loc.id})");
        }
    }

    async Task Connect()
    {
        await client.GetLocations();
        if (client.Locations == null) {
            throw new Exception("Could not load location list");
        }

        var args = new ExpressVPNClient.ConnectArgs();
        // No location -> Connect to default location
        if (string.IsNullOrEmpty(location)) {
            if (string.IsNullOrEmpty(client.DefaultLocationId)) {
                throw new Exception("No default location returned");
            }
            
            var defaultLoc = client.GetLocation(client.DefaultLocationId);
            if (defaultLoc == null) {
                throw new Exception($"Default location with id {client.DefaultLocationId} not found in locations list");
            }

            Logger.LogInformation($"Connecting to default location '{defaultLoc.Value.name}'");
            args.id = defaultLoc.Value.id;
            args.name = defaultLoc.Value.name;
            args.is_default = true;

        } else {
            // Connect to any location in a country
            var countryLoc = client.Locations.FirstOrDefault(l => l.country == location || l.country_code == location);
            if (countryLoc.country != null) {
                Logger.LogInformation($"Connecting to best location in country '{countryLoc.country}'");
                args.country = countryLoc.country;
            
            // Look up specific location
            } else {
                ExpressVPNClient.Location selected = default;
                int selectedPriority = 0;
                foreach (var loc in client.Locations) {
                    // ID-match has the highest priority
                    if (selectedPriority < 2 && location == loc.id) {
                        selected = loc;
                        selectedPriority = 2;
                    // Name-match has lower priority
                    } else if (selectedPriority < 1 && loc.name.Contains(location, StringComparison.OrdinalIgnoreCase)) {
                        selected = loc;
                        selectedPriority = 1;
                    }
                }
                if (selected.name == null) {
                    throw new Exception($"Could not find a location for the query '{location}'");
                }
                Logger.LogInformation($"Connecting to location '{selected.name}'");
                args.id = selected.id;
                args.name = selected.name;
            }
        }

        await client.Connect(args, timeout);

        await client.UpdateStatus();

        Console.WriteLine($"Connected to '{client.LatestStatus.current_location.name}'");
    }

    async Task Disconnect()
    {
        await client.Disconnect(timeout);

        Console.WriteLine($"Disconnected");
    }

    async Task ReadInput()
    {
        while (true) {
            await Task.Run(() => input = Console.ReadLine());
        }
    }

    public async Task Repl()
    {
        _ = ReadInput();

        while (true) {
            await Task.Delay(20);

            if (input != null) {
                client.Send(input);
                input = null;
            }

            // {"jsonrpc": "2.0", "method": "XVPN.GetStatus", "params": {}, "id": 1}
            // {"jsonrpc": "2.0", "method": "XVPN.GetLocations", "params": { "include_default_location": true, "include_recent_connections": true }, "id": 1}
            // {"jsonrpc": "2.0", "method": "XVPN.Connect", "params": { "country": "Germany" }, "id": 1}
            // {"jsonrpc": "2.0", "method": "XVPN.Disconnect", "params": {}, "id": 1}
        }
    }

    #pragma warning disable CS0649

    [Serializable]
    struct AlfredResult
    {
        public IEnumerable<AlfredItem> items;
        public Dictionary<string, string> variables;
        public float? rerun;
    }

    [Serializable]
    struct AlfredIcon
    {
        public string type;
        public string path;
    }

    [Serializable]
    struct AlfredMods
    {
        public AlfredMod alt;
        public AlfredMod cmd;
    }

    [Serializable]
    struct AlfredMod
    {
        public bool valid;
        public string arg;
        public string subtitle;
        public AlfredIcon icon;
    }

    [Serializable]
    struct AlfredItem
    {
        public string uid;
        public string title;
        public string subtitle;
        public string arg;
        public AlfredIcon icon;
        public bool valid;
        public string match;
        public string autocomplete;
        public string type;
        public AlfredMods mods;
        // mods…
        // text…

        public static AlfredItem FromLocation(ExpressVPNClient client, ExpressVPNClient.Location loc, bool withUid = true)
        {
            var prefix = "";
            var action = $"connect {loc.id}";

            var connectedId = client.LatestStatus.current_location.id;
            if (loc.id == connectedId) {
                prefix = "⚡️ ";
                action = "disconnect";
            } else {
                if (loc.favorite)
                    prefix += "❤️";
                if (client.RecentLocationIds.Contains(loc.id))
                    prefix += "🕙";
                if (client.DefaultLocationId == loc.id)
                    prefix += "👍";
                if (prefix.Length > 0) prefix = prefix + " ";
            }

            string uid = null;
            if (withUid) uid = loc.id;

            return new AlfredItem() {
                uid = uid,
                title = loc.name,
                subtitle = $"{prefix}{loc.region} - {loc.country}",
                arg = action,
                icon = new AlfredIcon() {
                    path = $"./flags/{loc.country_code}.png",
                },
                valid = true,
                match = $"{loc.name} {loc.region} {loc.country_code}",
            };
        }
    }

    #pragma warning restore CS0649

    async Task Alfred()
    {
        await client.GetLocations();

        var items = new List<AlfredItem>();
        
        if (alfredLocations) {
            foreach (var loc in client.Locations) {
                items.Add(AlfredItem.FromLocation(client, loc));
            }

        } else {
            if (client.LatestStatus.state == ExpressVPNClient.State.connected) {
                var loc = client.LatestStatus.current_location;
                items.Add(AlfredItem.FromLocation(client, loc, withUid: false));
            }

            foreach (var loc in client.Locations.Where(l => l.favorite)) {
                if (loc.id == client.LatestStatus.current_location.id) continue;
                items.Add(AlfredItem.FromLocation(client, loc, withUid: false));
            }

            foreach (var id in client.RecentLocationIds) {
                var loc = client.GetLocation(id);
                if (loc.Value.favorite) continue;
                if (loc.Value.id == client.LatestStatus.current_location.id) continue;
                items.Add(AlfredItem.FromLocation(client, loc.Value, withUid: false));
            }

            var defaultLoc = client.GetLocation(client.DefaultLocationId);
            items.Add(AlfredItem.FromLocation(client, defaultLoc.Value, withUid: false));
        }

        var result = new AlfredResult() {
            items = items
        };

        var json = JsonConvert.SerializeObject(result, new JsonSerializerSettings() {
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.Indented
        });
        Console.WriteLine(json);
    }
}

}
