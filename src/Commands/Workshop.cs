using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.Internal;

namespace SteamFileDownloader;

/// <summary>
/// The Steam requests that workshop commands make, so they can be faked.
/// </summary>
internal interface IWorkshopSource
{
    /// <summary>
    /// Details of items, with the items of collections.
    /// </summary>
    Task<List<PublishedFileDetails>> GetPublishedFileDetailsAsync(IReadOnlyCollection<ulong> itemIDs);

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
/// Why an item has no files to download: the error for an item given on its own, the short reason for one skipped in a
/// collection, and whether it's for lack of access.
/// </summary>
internal sealed record ItemProblem(string Message, string SkipReason, bool NoAccess = false);

/// <summary>
/// Workshop items: an item's files are a manifest in the workshop depot of the app it's for, or for a legacy item one
/// file at a URL.
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
    /// Whether a legacy item says where its file is, with a web address, and what it's called.
    /// </summary>
    public static bool HasLegacyFile(PublishedFileDetails details) => LegacyItem.TryCreate(details, "") != null;

    /// <summary>
    /// Gets the details of items, then level by level those of the items in collections among them. Items Steam
    /// returned nothing for are missing.
    /// </summary>
    public static async Task<Dictionary<ulong, PublishedFileDetails>> GetDetailsAsync(IWorkshopSource source, IEnumerable<ulong> itemIDs)
    {
        var details = new Dictionary<ulong, PublishedFileDetails>();
        var seen = new HashSet<ulong>();
        var pending = itemIDs.Where(seen.Add).ToList();

        while (pending.Count > 0)
        {
            var batches = await Task.WhenAll(pending.Chunk(DetailsBatchSize).Select(batch => source.GetPublishedFileDetailsAsync(batch)));

            foreach (var item in batches.SelectMany(static b => b))
            {
                details.TryAdd(item.publishedfileid, item);
            }

            var nextLevel = new List<ulong>();

            foreach (var itemID in pending)
            {
                var item = details.GetValueOrDefault(itemID);

                if (!IsAvailable(item) || !IsCollection(item))
                {
                    continue;
                }

                foreach (var child in item.children)
                {
                    if (seen.Add(child.publishedfileid))
                    {
                        nextLevel.Add(child.publishedfileid);
                    }
                }
            }

            pending = nextLevel;
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

                var item = details.GetValueOrDefault(child.publishedfileid);

                if (IsAvailable(item) && IsCollection(item))
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
    /// Why an item has no files to download, or null when it has. Collections are expanded before this.
    /// </summary>
    public static ItemProblem? GetProblem(ulong itemID, PublishedFileDetails? details)
    {
        var result = details == null ? EResult.FileNotFound : (EResult)details.result;
        var appIdHint = itemID < SmallItemID ? $" If {itemID} is an app id, find its items with \"{Help.ExeName} workshop search {itemID} <text>\"." : "";

        return result switch
        {
            EResult.OK when IsLegacy(details!) && !HasLegacyFile(details!) => new($"Item {itemID} is a legacy workshop item without a file to download.", "without files"),
            EResult.OK => null,
            EResult.FileNotFound => new($"Item {itemID} does not exist.{appIdHint}", "removed"),
            EResult.AccessDenied => new($"Item {itemID} is private, or this account can't see it.", "private", NoAccess: true),
            _ => new($"Steam gave no details for item {itemID} ({result}).", "without details"),
        };
    }

    /// <summary>
    /// The app's workshop depot, which holds the files of its items, or 0 when it has none.
    /// </summary>
    public static uint GetWorkshopDepot(KeyValue? appInfo)
    {
        var depot = appInfo?["depots"]["workshopdepot"].Value;

        return uint.TryParse(depot, NumberStyles.None, CultureInfo.InvariantCulture, out var depotID) ? depotID : 0;
    }

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
    public static async Task<ulong> GetNewestManifestAsync(IWorkshopSource source, ulong itemID)
    {
        var history = await source.GetChangeHistoryAsync(itemID, 0, 1);

        return history.changes.FirstOrDefault()?.manifest_id ?? 0;
    }

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

    public static string FormatTime(uint unixTime)
    {
        if (unixTime == 0)
        {
            return "-";
        }

        return DateTimeOffset.FromUnixTimeSeconds(unixTime).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The columns shared by the rows of info and search: item, app, depot, manifest, updated and size.
    /// </summary>
    public static string FormatColumns(PublishedFileDetails details, uint depotID, string manifest)
    {
        var depot = depotID == 0 ? "-" : depotID.ToString(CultureInfo.InvariantCulture);

        return string.Join('\t',
            details.publishedfileid.ToString(CultureInfo.InvariantCulture),
            details.consumer_appid.ToString(CultureInfo.InvariantCulture),
            depot,
            manifest,
            FormatTime(details.time_updated),
            details.file_size.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The manifest column when it's known without the change history: "collection", "legacy", the manifest, or null.
    /// </summary>
    public static string? GetManifestColumn(PublishedFileDetails details)
    {
        if (IsCollection(details))
        {
            return "collection";
        }

        if (IsLegacy(details))
        {
            return "legacy";
        }

        if (details.hcontent_file != 0)
        {
            return details.hcontent_file.ToString(CultureInfo.InvariantCulture);
        }

        return null;
    }
}
