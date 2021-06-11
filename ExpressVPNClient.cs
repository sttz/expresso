using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Threading.Tasks;
using System.Runtime.InteropServices;

namespace sttz.expresso
{

/// <summary>
/// Client to talk to the ExpressVPN browser helper.
/// </summary>
public class ExpressVPNClient : NativeMessagingClient
{

    
    /// <summary>
    /// ExpressVPN state.
    /// </summary>
    public enum State
    {
        activated,
        ready,
        connecting, 
        reconnecting, 
        connected, 
        disconnecting, 
        internal_error,
        network_error,
        fraudster,
        subscription_expired,
        license_revoked,
        activation_error,
        duplicate_license_used,
        connection_error,
        not_activated,
    }

    #pragma warning disable CS0649

    /// <summary>
    /// Result returned from "XVPN.GetStatus".
    /// </summary>
    [Serializable]
    public struct StatusResult
    {
        public StatusInfo info;
    }

    [Serializable]
    public struct StatusInfo
    {
        public State state;
        public Location current_location;
        public SelectedLocation selected_location;
        public Location last_location;
        public string latest_version;
        public string latest_version_url;
    }

    [Serializable]
    public struct SelectedLocation
    {
        public bool is_country;
        public bool is_smart_location;
        public string name;
        public string id;
    }

    [Serializable]
    public struct Location
    {
        public string country;
        public string country_code;
        public bool favorite;
        public string icon;
        public string id;
        public DateTime last_connected_time;
        public string name;
        public string protocols;
        public bool recommended;
        public string region;
        public int sort_order;
        public DateTime update_time;
    }

    /// <summary>
    /// Result returned from "XVPN.GetLocations".
    /// </summary>
    [Serializable]
    public struct GetLocationsResult
    {
        public Location default_location;
        public Location[] locations;
        public string[] recent_locations_ids;
        public string[] recommended_location_ids;
    }

    /// <summary>
    /// Arguments for "XVPN.SelectLocation".
    /// </summary>
    public struct SelectArgs
    {
        public SelectedLocation selected_location;
    }

    /// <summary>
    /// Arguments for "XVPN.Connect".
    /// </summary>
    [Serializable]
    public struct ConnectArgs
    {
        public string country;
        public string name;
        public bool is_default;
        public string id;
        public bool change_connected_location;
        public bool is_auto_connect;
    }

    /// <summary>
    /// Structure of basic method calls.
    /// </summary>
    [Serializable]
    struct MethodCall<TParams>
    {
        public string jsonrpc;
        public string method;
        public TParams @params;
        public int id;
    }

    /// <summary>
    /// Arguments for methods with no arguments.
    /// </summary>
    [Serializable]
    struct NoParams {}

    #pragma warning restore CS0649

    /// <summary>
    /// Timeout waiting for response from helper (ms).
    /// </summary>
    public int ResponseTimeout { get; set; } = 500;

    /// <summary>
    /// Wether we're connected to the helper.
    /// </summary>
    public bool IsConnectedToHelper { get; private set; }
    /// <summary>
    /// The ExpressVPN app version we're connected to.
    /// </summary>
    /// <remarks>
    /// Only set when <see cref="IsConnectedToHelper"/> is `true`.
    /// </remarks>
    public string AppVersion { get; private set; }
    
    /// <summary>
    /// The last info returned from the helper in response of a <see cref="UpdateStatus"/> call.
    /// </summary>
    public StatusInfo LatestStatus { get; private set; }

    /// <summary>
    /// Event raised when the connection to the helper is established.
    /// </summary>
    public event Action ConnectedToHelper;
    /// <summary>
    /// Event raised when <see cref="LatestStatus"/> has been updated.
    /// </summary>
    public event Action StatusUpdate;
    /// <summary>
    /// Event raised when the status has been updated by the XVPN.GetStatus command.
    /// </summary>
    public event Action FullStatusUpdate;
    /// <summary>
    /// Event raised while connecting to a VPN location with the connection progress.
    /// </summary>
    /// <remarks>
    /// Event argument is the connection progress in the range 0-100.
    /// </remarks>
    public event Action<float> ConnectionProgress;

    /// <summary>
    /// Available VPN locations.
    /// </summary>
    /// <remarks>
    /// Only available after a <see cref="GetLocations"/> call.
    /// </remarks>
    public Location[] Locations { get; private set; }
    /// <summary>
    /// The id of the default «smart location».
    /// </summary>
    /// <remarks>
    /// Only available after a <see cref="GetLocations"/> call.
    /// </remarks>
    public string DefaultLocationId { get; private set; }
    /// <summary>
    /// The ids of locations ExpressVPN was recently connected to.
    /// </summary>
    /// <remarks>
    /// Only available after a <see cref="GetLocations"/> call.
    /// </remarks>
    public string[] RecentLocationIds { get; private set; }
    /// <summary>
    /// The ids of recommended VPN locations.
    /// </summary>
    /// <remarks>
    /// Only available after a <see cref="GetLocations"/> call.
    /// </remarks>
    public string[] RecommendedLocationIds { get; private set; }

    /// <summary>
    /// Event raised when the locations have been updated.
    /// </summary>
    public event Action LocationsUpdated;

    public static readonly string DefaultManifestName = 
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "com.expressvpn.helper.firefox" 
        : "com.expressvpn.helper";

    /// <summary>
    /// Create a new client and try to connect to the helper.
    /// </summary>
    /// <param name="logger">The instance to use for logging</param>
    /// <returns></returns>
    public ExpressVPNClient(ILogger logger) : base(DefaultManifestName, logger)
    {
        Task.Run(Process);
    }

    /// <summary>
    /// Wait for the connection to the helper to be established.
    /// </summary>
    /// <remarks>
    /// This is separate from the lower-level connection and part of how
    /// the ExpressVPN helper signals it's ready.
    /// </remarks>
    /// <param name="timeout">Timeout for waiting for the connection</param>
    public async Task WaitForConnection(int timeout = 1000)
    {
        if (IsConnectedToHelper) return;

        var source = new TaskCompletionSource<bool>();
        Action handler = () => source.SetResult(true);
        ConnectedToHelper += handler;

        try {
            await WithTimeout(source.Task, timeout);
        } finally {
            ConnectedToHelper -= handler;
        }
    }

    /// <summary>
    /// Update the ExpressVPN status, stored in <see cref="LatestStatus"/>.
    /// </summary>
    public async Task UpdateStatus()
    {
        AssertHelperConnected();

        var source = new TaskCompletionSource<bool>();
        Action handler = () => source.SetResult(true);
        FullStatusUpdate += handler;

        Call("XVPN.GetStatus", new NoParams());

        try {
            await WithTimeout(source.Task);
        } finally {
            FullStatusUpdate -= handler;
        }
    }

    /// <summary>
    /// Update the ExpressVPN locations, stored in <see cref="Locations"/>.
    /// </summary>
    public async Task GetLocations()
    {
        AssertHelperConnected();

        var source = new TaskCompletionSource<bool>();
        Action handler = () => source.SetResult(true);
        LocationsUpdated += handler;

        Call("XVPN.GetLocations", new NoParams());

        try {
            await WithTimeout(source.Task);
        } finally {
            LocationsUpdated -= handler;
        }
    }

    /// <summary>
    /// Find a location by its id.
    /// </summary>
    public Location? GetLocation(string id)
    {
        if (Locations == null) throw new Exception("Locations have not been loaded yet.");

        foreach (var loc in Locations) {
            if (loc.id == id) return loc;
        }

        return null;
    }

    /// <summary>
    /// Connect to a ExpressVPN location.
    /// </summary>
    /// <param name="args">Connection arguments</param>
    /// <param name="timeout">Timeout waiting for the connection to be established</param>
    public async Task Connect(ConnectArgs args, int timeout = 10000)
    {
        AssertHelperConnected();

        if (LatestStatus.state != State.connected) {
            // A SelectLocation call is required for the browser extension
            // to show the correct location
            var selected = new SelectedLocation() {
                id = args.id,
                name = string.IsNullOrEmpty(args.country) ? args.name : args.country,
                is_country = !string.IsNullOrEmpty(args.country),
                is_smart_location = args.is_default
            };

            if (LatestStatus.selected_location.name != selected.name) {
                var source = new TaskCompletionSource<bool>();
                Action handler = () => source.SetResult(true);
                StatusUpdate += handler;

                Call("XVPN.SelectLocation", new SelectArgs() {
                    selected_location = new SelectedLocation() {
                        id = args.id,
                        name = string.IsNullOrEmpty(args.country) ? args.name : args.country,
                        is_country = !string.IsNullOrEmpty(args.country),
                        is_smart_location = args.is_default
                    }
                });

                try {
                    await WithTimeout(source.Task);
                } finally {
                    StatusUpdate -= handler;
                }
            }
        }

        Call("XVPN.Connect", args);

        float? progress = null;

        Action<float> progressHandler = (p) => progress = p;
        ConnectionProgress += progressHandler;

        var lastStateWasConnected = (LatestStatus.state == State.connected);
        var start = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        while (
            (
                LatestStatus.state == State.ready 
                || LatestStatus.state == State.connecting
                || LatestStatus.state == State.disconnecting
                || (lastStateWasConnected && LatestStatus.state == State.connected)
            )
            && (DateTimeOffset.Now.ToUnixTimeMilliseconds() - start) < timeout
        ) {
            await Task.Delay(20);
            if (progress != null) {
                Log.LogInformation($"Connecting... {progress:0.##}%");
                progress = null;
            }
            if (lastStateWasConnected &&
                    (LatestStatus.state != State.connected && LatestStatus.state != State.disconnecting)
            ) {
                lastStateWasConnected = false;
            }
        }
        ConnectionProgress -= progressHandler;

        if (LatestStatus.state == State.connected) {
            Log.LogInformation($"Finished connection with state: {LatestStatus.state}");
        } else if (LatestStatus.state == State.ready || LatestStatus.state == State.connecting) {
            throw new TimeoutException($"Timed out waiting to connect.");
        } else {
            throw new Exception($"Error while connecting, ended up in state '{LatestStatus.state}'");
        }
    }

    /// <summary>
    /// Disconnect from the current ExpressVPN location.
    /// </summary>
    /// <param name="timeout">Timeout waiting for the disconnect.</param>
    public async Task Disconnect(int timeout = 10000)
    {
        AssertHelperConnected();
        if (LatestStatus.state != State.connected) throw new Exception("VPN is not connected");

        Call("XVPN.Disconnect", new NoParams());

        var start = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        while (
            (LatestStatus.state == State.connected || LatestStatus.state == State.disconnecting)
            && (DateTimeOffset.Now.ToUnixTimeMilliseconds() - start) < timeout
        ) {
            await Task.Delay(200);
        }

        if (LatestStatus.state == State.ready) {
            Log.LogInformation($"Disconnected successfully");
        } else if (LatestStatus.state == State.connected || LatestStatus.state == State.disconnecting) {
            throw new TimeoutException($"Timed out waiting to disconnect.");
        } else {
            throw new Exception($"Error while disconnecting, ended up in state '{LatestStatus.state}'");
        }
    }

    /// <summary>
    /// The loop used to process incoming messages.
    /// </summary>
    async void Process()
    {
        while (true) {
            await Task.Delay(20);
            
            string message;
            while ((message = Receive()) != null) {
                try {
                    var doc = JObject.Parse(message);
                    if (doc.TryGetValue("error", out var error)) {
                        Log.LogError(error.ToString());
                        // handle error
                    
                    } else if (doc.TryGetValue("connected", out var connected) && (bool)connected) {
                        IsConnectedToHelper = true;
                        AppVersion = doc.Value<string>("app_version");
                        ConnectedToHelper?.Invoke();
                    
                    } else if (doc.TryGetValue("info", out var _)) {
                        var status = JsonConvert.DeserializeObject<StatusResult>(message);
                        LatestStatus = status.info;
                        StatusUpdate?.Invoke();
                        FullStatusUpdate?.Invoke();
                    
                    } else if (doc.TryGetValue("name", out var name)) {
                        var messageName = (string)name;
                        var data = doc.Value<JObject>("data")?.Value<JObject>($"{messageName}Data");
                        if (messageName == "ServiceStateChanged") {
                            var stateName = data.Value<string>("newstate");
                            if (!Enum.TryParse<State>(stateName, true, out State state)) {
                                Log.LogError($"Unknown state in ServiceStateChanged: {stateName}");
                            } else {
                                var info = LatestStatus;
                                info.state = state;
                                LatestStatus = info;
                                StatusUpdate?.Invoke();
                            }
                        } else if (messageName == "ConnectionProgress") {
                            var progress = data.Value<float>("progress");
                            ConnectionProgress?.Invoke(progress);
                        } else if (messageName == "SelectedLocationChanged") {
                            var info = LatestStatus;
                            var selectedLocation = info.selected_location;
                            selectedLocation.id = data.Value<string>("id");
                            selectedLocation.name = data.Value<string>("name");
                            selectedLocation.is_country = data.Value<bool>("is_country");
                            selectedLocation.is_smart_location = data.Value<bool>("is_smart_location");
                            info.selected_location = selectedLocation;
                            LatestStatus = info;
                            StatusUpdate?.Invoke();
                        } else if (messageName == "WaitForNetworkReady") {
                            // Ignore WaitForNetworkReady
                        } else {
                            Log.LogWarning($"Unhandled named message: {messageName}");
                        }
                    
                    } else if (doc.TryGetValue("Preferences", out var _)) {
                        // handle preferences change
                    
                    } else if (doc.TryGetValue("locations", out var _)) {
                        var result = JsonConvert.DeserializeObject<GetLocationsResult>(message);
                        Locations = result.locations;
                        DefaultLocationId = result.default_location.id;
                        RecentLocationIds = result.recent_locations_ids;
                        RecommendedLocationIds = result.recommended_location_ids;
                        LocationsUpdated?.Invoke();
                    
                    } else if (doc.TryGetValue("messages", out var _)) {
                        // handle messages
                    
                    } else if (doc.TryGetValue("success", out var _)) {
                        // Ignore useless success messages

                    } else {
                        Log.LogWarning($"Unhandled message: " + message);
                    }
                } catch (Exception e) {
                    Log.LogError($"Exception handling response: {e}");
                }
            }
        }
    }

    /// <summary>
    /// Low-level method call.
    /// </summary>
    void Call<TParams>(string method, TParams pms)
    {
        var json = JsonConvert.SerializeObject(new MethodCall<TParams>() {
            jsonrpc = "2.0",
            method = method,
            @params = pms,
            id = 1
        });
        Send(json);
    }

    void AssertHelperConnected()
    {
        if (!IsConnectedToHelper) throw new Exception("Connection to helper has not been established.");
    }

    async Task WithTimeout(Task task, int timeout = -1)
    {
        if (await Task.WhenAny(task, Task.Delay(timeout >= 0 ? timeout : ResponseTimeout)) != task) {
            throw new TimeoutException("Timed out waiting for the result.");
        }
    }
}

/*
Requests the current status of the expressVPN application
XVPN.GetStatus

Gets current chrome settings from the xVPN service
XVPN.GetBrowserPreferences

Gets engine settings
XVPN.GetEnginePreferences

Requests the current list of locations
XVPN.GetLocations { "include_default_location": true, "include_recent_connections": true }

Requests the expressVPN's connection logs
XVPN.GetLogs

Cancels the speed test
XVPN.StopSpeedTest

Called when user selects a new location.
XVPN.SelectLocation {
    selected_location: {
        name: selectedLocation.name,
        is_country: selectedLocation.is_country,
        is_smart_location: selectedLocation.is_smart_location,
        id: selectedLocation.id,
    },
}

Connects to the location passed in the parameter
XVPN.Connect {
    country: location.name,
    is_default: location.is_smart_location,
    id: location.id,
    change_connected_location: connectWhileConnected,
    is_auto_connect: isAutoConnect
}

Called when user click 'Back to Home' in the connection failed screen. to the location passed
XVPN.ResetState

Logs out the current user
XVPN.Reset

Opens the Location picker dialog
XVPNUI.OpenLocationPicker

Open preferences window and change to "Chrome" tab in native Mac/Windows apps.
XVPNUI.OpenChromePreferences

Open preferences window in native Mac/Windows apps.
XVPNUI.OpenPreferences

Disconnects from the current connections
XVPN.Disconnect

Returns a list of messages to display to the user in the footer
XVPN.GetMessages

Retry connect to last selected location. This command should only be used in connection_error state.
XVPN.RetryConnect

Opens native app
XVPNUI.LaunchApp
*/

}
