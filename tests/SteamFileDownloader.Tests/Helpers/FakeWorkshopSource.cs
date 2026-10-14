using SteamKit2;
using SteamKit2.Internal;

namespace SteamFileDownloader.Tests;

/// <summary>
/// Workshop items, change histories and apps held in memory, recording the requests made for them.
/// </summary>
internal sealed class FakeWorkshopSource : IWorkshopSource
{
    public Dictionary<ulong, PublishedFileDetails> Items = [];
    public Dictionary<ulong, List<CPublishedFile_GetChangeHistory_Response.ChangeLog>> Histories = [];
    public Dictionary<uint, KeyValue> Apps = [];
    public List<ulong[]> DetailsRequests = [];
    public List<(ulong ItemID, uint StartIndex, uint Count)> HistoryRequests = [];
    public List<PublishedFileDetails> SearchResults = [];
    public List<CPublishedFile_QueryFiles_Request> QueryRequests = [];

    // Steam's own cap on items per search page
    public int SearchPageCap = 100;

    public static PublishedFileDetails Item(ulong id, ulong manifest = 0, uint app = 730, uint flags = Workshop.FlagDepotContent, string title = "") => new()
    {
        result = (uint)EResult.OK,
        publishedfileid = id,
        consumer_appid = app,
        hcontent_file = manifest,
        flags = flags,
        title = title,
    };

    public static PublishedFileDetails Collection(ulong id, params (ulong ID, uint SortOrder)[] children)
    {
        var collection = Item(id, flags: 0);
        collection.file_type = Workshop.FileTypeCollection;
        collection.children.AddRange(children.Select(static c => new PublishedFileDetails.Child { publishedfileid = c.ID, sortorder = c.SortOrder }));

        return collection;
    }

    public static PublishedFileDetails Unavailable(ulong id, EResult result) => new() { result = (uint)result, publishedfileid = id };

    public static CPublishedFile_GetChangeHistory_Response.ChangeLog Change(ulong manifest, uint timestamp, string note = "") =>
        new() { manifest_id = manifest, timestamp = timestamp, change_description = note };

    public FakeWorkshopSource Add(params PublishedFileDetails[] items)
    {
        foreach (var item in items)
        {
            Items[item.publishedfileid] = item;
        }

        return this;
    }

    public FakeWorkshopSource AddApp(uint appID, uint workshopDepot)
    {
        Apps[appID] = KeyValue.LoadFromString($$"""
            "appinfo"
            {
                "depots"
                {
                    "workshopdepot" "{{workshopDepot}}"
                }
            }
            """)!;

        return this;
    }

    public Task<List<PublishedFileDetails>> GetPublishedFileDetailsAsync(IReadOnlyCollection<ulong> itemIDs)
    {
        DetailsRequests.Add([.. itemIDs]);

        // Steam answers every id, with FileNotFound for unknown ones
        return Task.FromResult(itemIDs.Select(id => Items.GetValueOrDefault(id) ?? Unavailable(id, EResult.FileNotFound)).ToList());
    }

    public Task<CPublishedFile_GetChangeHistory_Response> GetChangeHistoryAsync(ulong itemID, uint startIndex, uint count)
    {
        HistoryRequests.Add((itemID, startIndex, count));

        var changes = Histories.GetValueOrDefault(itemID) ?? [];
        var response = new CPublishedFile_GetChangeHistory_Response { total = (uint)changes.Count };
        response.changes.AddRange(changes.Skip((int)startIndex).Take((int)count));

        return Task.FromResult(response);
    }

    // Search results by cursor: "*" first, then the item id after which a page starts
    public Task<CPublishedFile_QueryFiles_Response> QueryFilesAsync(CPublishedFile_QueryFiles_Request request)
    {
        QueryRequests.Add(request);

        var start = request.cursor == "*" ? 0 : SearchResults.FindIndex(d => d.publishedfileid.ToString() == request.cursor) + 1;
        var page = SearchResults.Skip(start).Take((int)Math.Min(request.numperpage, SearchPageCap)).ToList();
        var response = new CPublishedFile_QueryFiles_Response
        {
            total = (uint)SearchResults.Count,
            next_cursor = page.Count > 0 ? page[^1].publishedfileid.ToString() : request.cursor,
        };
        response.publishedfiledetails.AddRange(page);

        return Task.FromResult(response);
    }

    public Task<Dictionary<uint, KeyValue>> GetAppInfosAsync(IReadOnlyCollection<uint> appIDs) =>
        Task.FromResult(appIDs.Where(Apps.ContainsKey).ToDictionary(static a => a, a => Apps[a]));
}
