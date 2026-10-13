using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ProtoBuf;
using SteamKit2;
using SteamKit2.Internal;

namespace SteamFileDownloader;

/// <summary>
/// The Steam requests that workshop commands make, so they can be faked.
/// </summary>
internal interface IWorkshopSource
{
    /// <summary>
    /// Details of items, with the items of collections. Full details add tags, key-value tags, metadata, votes and previews.
    /// </summary>
    Task<List<PublishedFileDetails>> GetPublishedFileDetailsAsync(IReadOnlyCollection<ulong> itemIDs, bool full);

    /// <summary>
    /// A page of an item's changes, newest first.
    /// </summary>
    Task<CPublishedFile_GetChangeHistory_Response> GetChangeHistoryAsync(ulong itemID, uint startIndex, uint count);

    Task<CPublishedFile_QueryFiles_Response> QueryFilesAsync(CPublishedFile_QueryFiles_Request request);

    Task<Dictionary<uint, KeyValue>> GetAppInfosAsync(IReadOnlyCollection<uint> appIDs);
}

/// <summary>
/// A version of an item: the manifest it uploaded, when, and its change note.
/// </summary>
internal sealed record WorkshopVersion(ulong ManifestID, uint Timestamp, string Note);

/// <summary>
/// Workshop items: an item's files are a manifest in the workshop depot of the app it's for.
/// </summary>
internal static class Workshop
{
    // EWorkshopFileType.Collection
    public const uint FileTypeCollection = 2;

    // The item's files are a manifest of the workshop depot, rather than a legacy UGC file
    public const uint FlagDepotContent = 0x1000;

    private const int DetailsBatchSize = 100;

    // Steam returns up to this many changes per request
    public const uint HistoryPageSize = 1000;

    // Below this an id is more likely an app id than an item
    private const ulong SmallItemID = 10_000_000;

    public static bool IsAvailable([NotNullWhen(true)] PublishedFileDetails? details) => details != null && (EResult)details.result == EResult.OK;

    public static bool IsCollection(PublishedFileDetails details) => details.file_type == FileTypeCollection;

    public static bool IsLegacy(PublishedFileDetails details) => (details.flags & FlagDepotContent) == 0;

    /// <summary>
    /// Gets the details of items, then level by level those of the items in collections among them. Items Steam
    /// returned nothing for are missing.
    /// </summary>
    public static async Task<Dictionary<ulong, PublishedFileDetails>> GetDetailsAsync(IWorkshopSource source, IEnumerable<ulong> itemIDs, bool full = false)
    {
        var details = new Dictionary<ulong, PublishedFileDetails>();
        var seen = new HashSet<ulong>();
        var pending = itemIDs.Where(seen.Add).ToList();

        while (pending.Count > 0)
        {
            var batches = await Task.WhenAll(pending.Chunk(DetailsBatchSize).Select(batch => source.GetPublishedFileDetailsAsync(batch, full)));

            foreach (var item in batches.SelectMany(static b => b))
            {
                details.TryAdd(item.publishedfileid, item);
            }

            pending = [.. pending
                .Select(details.GetValueOrDefault)
                .Where(static d => IsAvailable(d) && IsCollection(d))
                .SelectMany(static d => d!.children)
                .Select(static c => c.publishedfileid)
                .Where(seen.Add)];
        }

        return details;
    }

    /// <summary>
    /// The items of a collection in its order, with the items of collections in it in their place, each once.
    /// </summary>
    public static List<ulong> GetCollectionItems(PublishedFileDetails collection, IReadOnlyDictionary<ulong, PublishedFileDetails> details)
    {
        var items = new List<ulong>();
        var visited = new HashSet<ulong> { collection.publishedfileid };

        Add(collection);

        return items;

        void Add(PublishedFileDetails parent)
        {
            foreach (var child in parent.children.OrderBy(static c => c.sortorder))
            {
                if (!visited.Add(child.publishedfileid))
                {
                    continue;
                }

                if (details.GetValueOrDefault(child.publishedfileid) is { } item && IsAvailable(item) && IsCollection(item))
                {
                    Add(item);
                }
                else
                {
                    items.Add(child.publishedfileid);
                }
            }
        }
    }

    /// <summary>
    /// Why an item has no files to download, and whether that's for lack of access, or null when it has. Collections
    /// are expanded before this.
    /// </summary>
    public static (string Problem, bool NoAccess)? GetProblem(ulong itemID, PublishedFileDetails? details)
    {
        var result = details == null ? EResult.FileNotFound : (EResult)details.result;

        return result switch
        {
            EResult.OK when IsLegacy(details!) => ($"Item {itemID} is a legacy (non-SteamPipe) workshop item, which is not implemented.", false),
            EResult.OK => null,
            EResult.FileNotFound => ($"Item {itemID} does not exist.{(itemID < SmallItemID ? $" If {itemID} is an app id, find its items with \"{Help.ExeName} workshop search {itemID} <text>\"." : "")}", false),
            EResult.AccessDenied => ($"Item {itemID} is private, or this account can't see it.", true),
            _ => ($"Steam gave no details for item {itemID} ({result}).", false),
        };
    }

    /// <summary>
    /// The app's workshop depot, which holds the files of its items, or 0 when it has none.
    /// </summary>
    public static uint GetWorkshopDepot(KeyValue? appInfo) =>
        uint.TryParse(appInfo?["depots"]["workshopdepot"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var depotID) ? depotID : 0;

    /// <summary>
    /// Gets the changes of an item's newest versions, up to limit, and its number of changes. Pages until a version past
    /// the limit shows up, since only then is the last one's oldest change known.
    /// </summary>
    public static async Task<(uint Total, List<CPublishedFile_GetChangeHistory_Response.ChangeLog> Changes)> GetHistoryAsync(
        IWorkshopSource source,
        ulong itemID,
        int limit)
    {
        var changes = new List<CPublishedFile_GetChangeHistory_Response.ChangeLog>();
        var manifests = new HashSet<ulong>();

        while (true)
        {
            var page = await source.GetChangeHistoryAsync(itemID, (uint)changes.Count, HistoryPageSize);

            foreach (var change in page.changes)
            {
                if (manifests.Count == limit && !manifests.Contains(change.manifest_id))
                {
                    return (page.total, changes);
                }

                manifests.Add(change.manifest_id);
                changes.Add(change);
            }

            if (page.changes.Count == 0 || changes.Count >= page.total)
            {
                return (page.total, changes);
            }
        }
    }

    /// <summary>
    /// The newest manifest in an item's history, or 0. Items sold in the game have their manifest only there.
    /// </summary>
    public static async Task<ulong> GetNewestManifestAsync(IWorkshopSource source, ulong itemID) =>
        (await source.GetChangeHistoryAsync(itemID, 0, 1)).changes.FirstOrDefault()?.manifest_id ?? 0;

    /// <summary>
    /// The versions in changes listed newest first. Changes that only edited the item's text keep its manifest, so each
    /// version is dated to its manifest's oldest change, which uploaded it, with that change's note.
    /// </summary>
    public static List<WorkshopVersion> CollapseHistory(IEnumerable<CPublishedFile_GetChangeHistory_Response.ChangeLog> changes) =>
        [.. changes
            .GroupBy(static c => c.manifest_id)
            .Select(static g => g.Last())
            .Select(static c => new WorkshopVersion(c.manifest_id, c.timestamp, c.change_description ?? ""))];

    /// <summary>
    /// Text that fits in a tab-separated row: control characters like tabs and line breaks, and runs of whitespace, become one space.
    /// </summary>
    public static string CleanText(string? text)
    {
        var builder = new StringBuilder(text?.Length ?? 0);

        foreach (var c in text ?? "")
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                if (builder.Length > 0 && builder[^1] != ' ')
                {
                    builder.Append(' ');
                }
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString().TrimEnd();
    }

    public static string FormatTime(uint unixTime) =>
        unixTime == 0 ? "-" : DateTimeOffset.FromUnixTimeSeconds(unixTime).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// The columns shared by the rows of info and search: item, app, depot, manifest, updated and size.
    /// </summary>
    public static string FormatColumns(PublishedFileDetails details, uint depotID, string manifest) => string.Join('\t',
        details.publishedfileid.ToString(CultureInfo.InvariantCulture),
        details.consumer_appid.ToString(CultureInfo.InvariantCulture),
        depotID == 0 ? "-" : depotID.ToString(CultureInfo.InvariantCulture),
        manifest,
        FormatTime(details.time_updated),
        details.file_size.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// The manifest column when it's known without the change history: "collection", "legacy", the manifest, or null.
    /// </summary>
    public static string? GetManifestColumn(PublishedFileDetails details) =>
        IsCollection(details) ? "collection"
        : IsLegacy(details) ? "legacy"
        : details.hcontent_file != 0 ? details.hcontent_file.ToString(CultureInfo.InvariantCulture)
        : null;

    /// <summary>
    /// Writes a protobuf message as a JSON object with the fields Steam sent, named as in the proto. 64-bit integers are
    /// strings, so they survive JSON parsers that read numbers as doubles, and enums are their names.
    /// </summary>
    public static void WriteJson(Utf8JsonWriter writer, object message)
    {
        writer.WriteStartObject();

        foreach (var property in message.GetType().GetProperties())
        {
            if (property.GetCustomAttribute<ProtoMemberAttribute>() is not { } member)
            {
                continue;
            }

            var shouldSerialize = message.GetType().GetMethod($"ShouldSerialize{property.Name}", Type.EmptyTypes);

            if (shouldSerialize != null && !(bool)shouldSerialize.Invoke(message, null)!)
            {
                continue;
            }

            var value = property.GetValue(message);

            if (value is null or IList { Count: 0 })
            {
                continue;
            }

            writer.WritePropertyName(member.Name ?? property.Name);
            WriteJsonValue(writer, value);
        }

        writer.WriteEndObject();
    }

    private static void WriteJsonValue(Utf8JsonWriter writer, object value)
    {
        switch (value)
        {
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case Enum:
                writer.WriteStringValue(value.ToString());
                break;
            case ulong or long:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
            case uint number:
                writer.WriteNumberValue(number);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case float number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case byte[] bytes:
                writer.WriteBase64StringValue(bytes);
                break;
            case IList list:
                writer.WriteStartArray();

                foreach (var item in list)
                {
                    WriteJsonValue(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                WriteJson(writer, value);
                break;
        }
    }
}
