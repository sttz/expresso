using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace sttz.expresso
{

/// <summary>
/// C# client to communicate with native browser extension helpers,
/// using Firefox/Chrome's messaging protocol.
/// </summary>
public class NativeMessagingClient
{
    protected ILogger Log;

    /// <summary>
    /// Paths where the native messaging manifests are stored.
    /// </summary>
    static readonly string[] manifestBasePaths = 
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? new string[] {
                "~/Library/Application Support/Mozilla/NativeMessagingHosts",
                "/Library/Application Support/Mozilla/NativeMessagingHosts",
            } :
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? new string[] {
                "/usr/lib/mozilla/native-messaging-hosts",
                "/usr/lib64/mozilla/native-messaging-hosts",
                "~/.mozilla/native-messaging-hosts", 
            } :
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? new string[] {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) + "/ExpressVPN/expressvpnd",
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + "/ExpressVPN/expressvpnd",
            } : new string[] { // If we can't detect the OS, try all the folders
                "~/Library/Application Support/Mozilla/NativeMessagingHosts",
                "/Library/Application Support/Mozilla/NativeMessagingHosts",
                "/usr/lib/mozilla/native-messaging-hosts",
                "/usr/lib64/mozilla/native-messaging-hosts",
                "~/.mozilla/native-messaging-hosts", 
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) + "/ExpressVPN/expressvpnd",
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + "/ExpressVPN/expressvpnd",
            };
    

    /// <summary>
    /// Extension of the native messaging manifest file.
    /// </summary>
    const string manifestExtension = ".json";

    #pragma warning disable CS0649

    /// <summary>
    /// Contents of the JSON-formatted native messaging manifest.
    /// </summary>
    [Serializable]
    struct NativeMessagingManifest
    {
        public string name;
        public string description;
        public string path;
        public string type;
        public string[] allowed_extensions;
    }

    #pragma warning restore CS0649

    string manifestPath;
    NativeMessagingManifest manifest;

    Process helper;
    ConcurrentQueue<string> outputQueue = new ConcurrentQueue<string>();

    /// <summary>
    /// Create a new client with the given application name.
    /// A native messaging manifest with a matching name must be installed.
    /// </summary>
    /// <param name="name">Name of the application or manifest</param>
    public NativeMessagingClient(string name, ILogger logger)
    {
        Log = logger;

        foreach (var basePath in manifestBasePaths) {
            var path = Path.Combine(basePath, name + manifestExtension);
            if (path.StartsWith("~/")) {
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), path.Substring(2));
            }
            if (File.Exists(path)) {
                manifestPath = path;
                break;
            }
        }

        if (manifestPath == null) {
            throw new Exception($"No manifest found with name: {name}");
        }

        var json = File.ReadAllText(manifestPath);
        manifest = JsonConvert.DeserializeObject<NativeMessagingManifest>(json);

        if (manifest.type != "stdio") {
            throw new Exception($"Unsupported native message type '{manifest.type}', only stdio is supported.");
        }

        if (!File.Exists(manifest.path)) {
            throw new Exception($"Helper specified in '{manifestPath}' does not exist: {manifest.path}");
        }

        Log.LogInformation($"Manifest loaded for {manifest.name} with helper at: {manifest.path}");

        Task.Run(ReceiveLoop);
    }

    /// <summary>
    /// Send a message to the native helper.
    /// </summary>
    /// <param name="json">Serialized JSON message</param>
    public async void Send(string json)
    {
        Log.LogDebug($"-> {json}");
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        var lengthBuf = BitConverter.GetBytes((uint)bytes.Length);
        var stdin = helper.StandardInput.BaseStream;
        await stdin.WriteAsync(lengthBuf, 0, lengthBuf.Length);
        await stdin.WriteAsync(bytes, 0, bytes.Length);
        await stdin.FlushAsync();
    }

    /// <summary>
    /// Receive the latest message from the helper.
    /// This methods needs to be called multiple times for each received message.
    /// </summary>
    /// <returns>The JSON message or null if there are no pending messages</returns>
    public string Receive()
    {
        if (outputQueue.TryDequeue(out var json)) {
            return json;
        }
        return null;
    }

    async Task ReadOutput(byte[] buffer, int offset, int length)
    {
        var total = 0;
        var stdout = helper.StandardOutput.BaseStream;
        while (total < length) {
            var read = await stdout.ReadAsync(buffer, total, length - total);
            if (read == 0) {
                throw new EndOfStreamException($"Reached end of stream but expected {length - total} more bytes.");
            }
            total += read;
        }
    }

    async void ReceiveLoop()
    {
        helper = new Process();
        helper.StartInfo = new ProcessStartInfo() {
            FileName = manifest.path,
            Arguments = $"\"{manifestPath}\" \"${manifest.allowed_extensions[0]}\"", // TODO: Escape values
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };

        helper.Start();

        var lengthBuf = new byte[4];
        var dataBuf = new byte[262144]; // TODO: Flexible buffer?
        while (!helper.HasExited) {
            await ReadOutput(lengthBuf, 0, 4);
            var length = BitConverter.ToUInt32(lengthBuf, 0);

            if (length > dataBuf.Length) {
                throw new Exception($"Output buffer is too small to receive message: {dataBuf.Length} < {length}");
            }

            await ReadOutput(dataBuf, 0, (int)length);
            var json = Encoding.UTF8.GetString(dataBuf, 0, (int)length);

            Log.LogDebug($"<- {json}");

            outputQueue.Enqueue(json);
        }

        if (helper.ExitCode != 0) {
            Log.LogError($"Helper has exited with code {helper.ExitCode}");
        } else {
            Log.LogInformation($"Helper has exited with code {helper.ExitCode}");
        }
    }
}

}