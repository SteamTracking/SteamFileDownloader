using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;

namespace SteamFileDownloader;

/// <summary>
/// The depots command: the depots and branches of apps, with results on stdout.
/// </summary>
internal static class DepotsCommand
{
    /// <summary>
    /// Lists the depots of apps with their manifest on a branch, and the apps' branches on stderr.
    /// With --json, prints everything Steam has on the apps' depots and branches instead.
    /// </summary>
    public static async Task<int> ListDepotsAsync(DepotArguments arguments, CancellationToken cancellationToken)
    {
        var options = arguments.Options;
        var appIDs = new List<uint> { options.App!.Value };

        // The other arguments are more apps
        foreach (var arg in arguments.Targets)
        {
            if (!uint.TryParse(arg, NumberStyles.None, CultureInfo.InvariantCulture, out var appID))
            {
                Log.Error($"Unexpected argument \"{arg}\", depots takes app ids. {Help.UsageHint("depots")}");
                return ExitCodes.Failed;
            }

            if (!appIDs.Contains(appID))
            {
                appIDs.Add(appID);
            }
        }

        var credentials = options.Anonymous ? LogOnCredentials.Anonymous : AccountStore.LoadCredentials(options.Username);
        var (loggedOn, logOnExitCode) = await SteamSession.CreateLoggedOnAsync(credentials, cacheDepotKeys: true, cancellationToken);

        if (loggedOn == null)
        {
            return logOnExitCode;
        }

        using var session = loggedOn;

        var appInfos = await session.GetAppInfosAsync(appIDs);
        session.Disconnect();

        // Apps without info were already reported
        var depotsByApp = appIDs.Where(appInfos.ContainsKey).Select(appID => (AppID: appID, Depots: appInfos[appID]["depots"])).ToList();
        var success = depotsByApp.Count == appIDs.Count;

        if (options.Json)
        {
            Log.Result(ToJson(depotsByApp));
        }
        else
        {
            foreach (var (appID, depots) in depotsByApp)
            {
                success &= ListAppDepots(appID, depots, options.Branch);
            }
        }

        return success ? ExitCodes.Ok : ExitCodes.Failed;
    }

    // Returns false after an error
    internal static bool ListAppDepots(uint appID, KeyValue depots, string branchName)
    {
        if (!AppInfo.CheckBranch(depots, appID, branchName))
        {
            return false;
        }

        var branches = depots["branches"].Children
            .Select(static b => $"{b.Name} (build {b["buildid"].Value}{(b["pwdrequired"].AsBoolean() ? ", password" : "")}{FormatTime(b["timeupdated"].AsLong())})");

        Log.Info("");
        Log.Info($"App {appID} branches: {string.Join(", ", branches)}");

        if (!AppInfo.GetDepots(depots).Any())
        {
            Log.Error($"App {appID} has no depots. DLC and tools often keep their files in depots of their parent app.");
            return false;
        }

        foreach (var (depotID, depot) in AppInfo.GetDepots(depots))
        {
            var branch = branchName;
            var manifestID = AppInfo.GetManifestIdForBranch(depot, ref branch);
            var config = depot["config"];
            var notes = new List<string>();

            AddNote("name", depot["name"].Value);
            AddNote("os", config["oslist"].Value);
            AddNote("arch", config["osarch"].Value);
            AddNote("language", config["language"].Value);
            AddNote("dlc", depot["dlcappid"].Value);
            AddNote("shared from app", depot["depotfromapp"].Value);

            if (branch != branchName)
            {
                notes.Add($"manifest from {branch}");
            }

            Log.Result($"{appID}\t{depotID}\t{(manifestID == 0 ? "-" : manifestID.ToString(CultureInfo.InvariantCulture))}\t{depot["maxsize"].Value ?? "-"}\t{string.Join(", ", notes)}");

            void AddNote(string name, string? value)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    notes.Add($"{name} {value}");
                }
            }
        }

        return true;

        static string FormatTime(long unixTime) => unixTime > 0 ? $", {DateTimeOffset.FromUnixTimeSeconds(unixTime):yyyy-MM-dd}" : "";
    }

    // An object per app with its whole depots section. Values are strings as Steam sends them, keys with children are objects
    internal static string ToJson(List<(uint AppID, KeyValue Depots)> depotsByApp)
    {
        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();

            foreach (var (appID, depots) in depotsByApp)
            {
                writer.WritePropertyName(appID.ToString(CultureInfo.InvariantCulture));
                WriteObject(writer, depots);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());

        static void WriteObject(Utf8JsonWriter writer, KeyValue keyValue)
        {
            writer.WriteStartObject();

            foreach (var child in keyValue.Children)
            {
                if (child.Value == null)
                {
                    writer.WritePropertyName(child.Name ?? "");
                    WriteObject(writer, child);
                }
                else
                {
                    writer.WriteString(child.Name ?? "", child.Value);
                }
            }

            writer.WriteEndObject();
        }
    }
}
