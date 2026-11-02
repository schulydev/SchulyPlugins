using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Schuly.Plugin.Microsoft.Controllers;
using Schuly.Plugin.Microsoft.Infrastructure;
using Xunit;

namespace Schuly.Plugin.Microsoft.Tests
{
    /// <summary>The Teams / shared files, search, channel message and file text routes against a fake SchulyMicrosoft: no raw Graph id leaves, route ids open as the kind their answers were sealed with, foreign ids answer 404.</summary>
    public class TeamsFilesRoutesTests
    {
        private const string Team = "0a1b2c3d-4e5f-6789-abcd-ef0123456789";
        private const string Channel = "19:rawchannel01@thread.tacv2";
        private const string Drive = "b!RAWTEAMDRIVE01";
        private const string Item = "01RAWTEAMITEM01";
        private const string Parent = "01RAWTEAMPARENT02";
        private const string Folder = "01RAWTEAMFOLDER03";
        private const string OwnItem = "01RAWOWNITEM04";
        private const string Msg = "1730000000111";
        private const string Reply = "1730000000099";
        private const string User1 = "11111111-aaaa-bbbb-cccc-000000000011";
        private const string User2 = "11111111-aaaa-bbbb-cccc-000000000012";
        private const string MailMsg = "AAMkRAWSEARCHMAIL01=";
        private const string ChatId = "19:rawsearchchat01@thread.v2";
        private const string ChatMsg = "1730000000222";
        private const string Attachment = "rawchannelattachment01";

        private static readonly string[] RawIds = [Team, Channel, Drive, Item, Parent, Folder, OwnItem, Msg, Reply, User1, User2, MailMsg, ChatId, ChatMsg, Attachment];

        private static Harness Create()
        {
            var h = new Harness();
            var a = h.Service.Answers;
            var driveItem = $$$"""{"id":"{{{Item}}}","name":"Plan.xlsx","size":5,"file":{"mimeType":"application/x"},"webUrl":"https://school.sharepoint.com/Plan.xlsx","@microsoft.graph.downloadUrl":"https://school.sharepoint.com/dl?t=1","@odata.context":"https://graph.microsoft.com/v1.0/$metadata#x","parentReference":{"driveId":"{{{Drive}}}","id":"{{{Parent}}}","path":"/drives/{{{Drive}}}/root:/Deutsch"},"createdBy":{"user":{"id":"{{{User1}}}","displayName":"A"} } }""";
            a["teams"] = $$$"""[{"id":"{{{Team}}}","displayName":"BM23d","description":"d"}]""";
            a[$"teams/{Team}/channels"] = $$$"""[{"id":"{{{Channel}}}","displayName":"Deutsch","membershipType":"standard","description":null}]""";
            a[$"teams/{Team}/files"] = $$$"""{"driveId":"{{{Drive}}}","folder":{"id":"{{{Folder}}}","name":"Deutsch","path":"Deutsch"},"items":[{{{driveItem}}}]}""";
            a[$"drives/{Drive}/items/{Item}"] = driveItem;
            a[$"drives/{Drive}/items/{Item}/children"] = $"[{driveItem}]";
            a[$"drives/{Drive}/items/{Item}/content"] = "bytes:data";
            a[$"drives/{Drive}/items/{Item}/thumbnail"] = "bytes:png";
            a[$"drives/{Drive}/items/{Item}/text"] = """{"name":"Plan.xlsx","kind":"spreadsheet","text":"a,b","offset":0,"nextOffset":null,"totalChars":3,"truncated":false,"notes":[]}""";
            a[$"files/{OwnItem}/text"] = """{"name":"a.txt","kind":"text","text":"hi","offset":0,"nextOffset":null,"totalChars":2,"truncated":false,"notes":[]}""";
            a["convert/text"] = """{"name":"a.txt","kind":"text","text":"hi","offset":0,"nextOffset":null,"totalChars":2,"truncated":false,"notes":[]}""";
            a["files/shared"] = $$$"""[{"id":"{{{Item}}}","name":"S.docx","parentReference":{"driveId":"{{{Drive}}}","id":"{{{Parent}}}"},"shared":{"sharedBy":{"user":{"id":"{{{User2}}}","displayName":"B","email":"b@school.ch"}},"sharedDateTime":"2026-10-01T00:00:00Z"},"webUrl":"https://school.sharepoint.com/S.docx"}]""";
            a["files/recent"] = $$$"""[{"id":"{{{Item}}}","name":"R.docx","parentReference":{"driveId":"{{{Drive}}}","id":"{{{Parent}}}"},"lastModifiedBy":{"user":{"id":"{{{User2}}}","displayName":"B"} } }]""";
            a["files/search"] = $$$"""{"query":"x","hits":[{"type":"file","id":"{{{Item}}}","driveId":"{{{Drive}}}","title":"Plan.xlsx","snippet":"s","location":{"kind":"team","teamId":"{{{Team}}}","channelId":"{{{Channel}}}","path":"Deutsch","label":"Team > Deutsch"},"from":{"displayName":"A","email":"a@school.ch"}}],"from":0,"size":25,"moreAvailable":false,"nextFrom":null,"totals":{"files":1},"fallback":false,"errors":[]}""";
            a["search"] = $$$"""{"query":"x","hits":[{"type":"file","id":"{{{Item}}}","driveId":"{{{Drive}}}","title":"Plan.xlsx","location":{"kind":"team","teamId":"{{{Team}}}","channelId":"{{{Channel}}}"}},{"type":"mail","id":"{{{MailMsg}}}","title":"s","from":{"displayName":"A","email":"a@b.ch"},"location":{"kind":"mailbox"}},{"type":"chat","id":"{{{ChatMsg}}}","title":"c","location":{"kind":"chat","chatId":"{{{ChatId}}}"}},{"type":"channel","id":"{{{Msg}}}","title":"m","location":{"kind":"channel","teamId":"{{{Team}}}","channelId":"{{{Channel}}}"}}],"from":0,"size":25,"moreAvailable":false,"nextFrom":null,"totals":{"files":1,"messages":3},"fallback":false,"errors":[]}""";
            var message = $$$"""{"id":"{{{Msg}}}","replyToId":"{{{Reply}}}","from":{"user":{"displayName":"A","id":"{{{User1}}}"}},"createdDateTime":"2026-10-01T00:00:00Z","subject":null,"text":"hi","attachments":[{"id":"{{{Attachment}}}","name":"a.docx","contentType":"reference","webUrl":"https://school.sharepoint.com/a.docx"}],"replyCount":null,"webUrl":"https://teams.microsoft.com/l/message/x","messageType":"message"}""";
            a[$"teams/{Team}/channels/{Channel}/messages"] = $"[{message}]";
            a[$"teams/{Team}/channels/{Channel}/messages/{Msg}/replies"] = $"[{message}]";
            return h;
        }

        private static TeamsFilesController Ctl(Harness h, Guid? asUser = null) => h.Controller((u, l, r) => new TeamsFilesController(u, l, r, h.Sealer), asUser);

        private static JsonNode Body(IActionResult result) => JsonNode.Parse(JsonSerializer.Serialize(Assert.IsAssignableFrom<ObjectResult>(result).Value))!;

        // Links clients open in Microsoft's own apps carry Microsoft's ids by design and no route accepts them back.
        private static void StripLinks(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var name in obj.Select(p => p.Key).ToList())
                        if (name is "webUrl" or "@microsoft.graph.downloadUrl")
                            obj.Remove(name);
                        else
                            StripLinks(obj[name]);
                    break;
                case JsonArray array:
                    foreach (var child in array)
                        StripLinks(child);
                    break;
            }
        }

        private static string T(Harness h, string kind, string id) => h.Seal(kind, id);

        private static SearchRequest Query(string? team = null) => new("Masterplan", null, null, null, team);

        private static async Task<IReadOnlyList<(string Route, JsonNode Json)>> EveryAnswer(Harness h)
        {
            var c = Ctl(h);
            var team = T(h, IdKinds.Team, Team);
            var channel = T(h, IdKinds.Channel, Channel);
            var drive = T(h, IdKinds.Drive, Drive);
            var item = T(h, IdKinds.DriveItemOther, Item);
            return
            [
                ("teams", Body(await c.Teams(h.Account))),
                ("channels", Body(await c.Channels(h.Account, team))),
                ("team files", Body(await c.TeamFiles(h.Account, team, channel))),
                ("team files root", Body(await c.TeamFiles(h.Account, team))),
                ("item", Body(await c.Item(h.Account, drive, item))),
                ("children", Body(await c.Children(h.Account, drive, item))),
                ("drive text", Body(await c.DriveText(h.Account, drive, item))),
                ("own text", Body(await c.FileText(h.Account, T(h, IdKinds.DriveItem, OwnItem)))),
                ("shared", Body(await c.Shared(h.Account))),
                ("recent", Body(await c.Recent(h.Account))),
                ("files search", Body(await c.SearchFiles(h.Account, Query(team)))),
                ("search", Body(await c.Search(h.Account, Query(team)))),
                ("messages", Body(await c.Messages(h.Account, team, channel))),
                ("replies", Body(await c.Replies(h.Account, team, channel, T(h, IdKinds.ChannelMessage, Msg)))),
            ];
        }

        [Fact]
        public async Task No_raw_graph_id_appears_in_any_new_json_answer()
        {
            var h = Create();
            foreach (var (route, json) in await EveryAnswer(h))
            {
                StripLinks(json);
                var text = json.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                foreach (var raw in RawIds)
                    Assert.False(text.Contains(raw, StringComparison.Ordinal), $"{route} leaks {raw}: {text}");
                Assert.DoesNotContain("@odata", text);
                Assert.DoesNotContain("graph.microsoft.com", text);
            }
        }

        [Fact]
        public async Task Ids_are_sealed_with_the_kind_their_routes_take()
        {
            var h = Create();
            var ids = h.Scope();
            var c = Ctl(h);
            var teamS = T(h, IdKinds.Team, Team);
            var channelS = T(h, IdKinds.Channel, Channel);

            Assert.Equal(Team, ids.Open(IdKinds.Team, (string?)Body(await c.Teams(h.Account)).AsArray()[0]!["id"]));
            Assert.Equal(Channel, ids.Open(IdKinds.Channel, (string?)Body(await c.Channels(h.Account, teamS)).AsArray()[0]!["id"]));

            var files = Body(await c.TeamFiles(h.Account, teamS, channelS));
            Assert.Equal(Drive, ids.Open(IdKinds.Drive, (string?)files["driveId"]));
            Assert.Equal(Folder, ids.Open(IdKinds.DriveItemOther, (string?)files["folder"]!["id"]));
            var entry = files["items"]![0]!;
            Assert.Equal(Item, ids.Open(IdKinds.DriveItemOther, (string?)entry["id"]));
            Assert.Equal(Drive, ids.Open(IdKinds.Drive, (string?)entry["parentReference"]!["driveId"]));
            Assert.Equal(Parent, ids.Open(IdKinds.DriveItemOther, (string?)entry["parentReference"]!["id"]));
            Assert.Equal(User1, ids.Open(IdKinds.User, (string?)entry["createdBy"]!["user"]!["id"]));
            Assert.Equal("/drive/root:/Deutsch", (string?)entry["parentReference"]!["path"]);
            Assert.NotNull(Body(await c.Item(h.Account, (string?)files["driveId"]!, (string?)entry["id"]!))["@microsoft.graph.downloadUrl"]);

            foreach (var list in new[] { Body(await c.Shared(h.Account)), Body(await c.Recent(h.Account)) })
            {
                Assert.Equal(Item, ids.Open(IdKinds.DriveItemOther, (string?)list[0]!["id"]));
                Assert.Equal(Drive, ids.Open(IdKinds.Drive, (string?)list[0]!["parentReference"]!["driveId"]));
                Assert.Equal(User2, ids.Open(IdKinds.User, (string?)(list[0]!["shared"]?["sharedBy"]?["user"]?["id"] ?? list[0]!["lastModifiedBy"]!["user"]!["id"])));
            }

            var messages = Body(await c.Messages(h.Account, teamS, channelS)).AsArray();
            Assert.Equal(Msg, ids.Open(IdKinds.ChannelMessage, (string?)messages[0]!["id"]));
            Assert.Equal(Reply, ids.Open(IdKinds.ChannelMessage, (string?)messages[0]!["replyToId"]));
            Assert.Equal(User1, ids.Open(IdKinds.User, (string?)messages[0]!["from"]!["user"]!["id"]));
            Assert.Equal(Attachment, ids.Open(IdKinds.Opaque, (string?)messages[0]!["attachments"]![0]!["id"]));

            var hits = Body(await c.Search(h.Account, Query(teamS)))["hits"]!.AsArray();
            Assert.Equal(Item, ids.Open(IdKinds.DriveItemOther, (string?)hits[0]!["id"]));
            Assert.Equal(Drive, ids.Open(IdKinds.Drive, (string?)hits[0]!["driveId"]));
            Assert.Equal(Team, ids.Open(IdKinds.Team, (string?)hits[0]!["location"]!["teamId"]));
            Assert.Equal(Channel, ids.Open(IdKinds.Channel, (string?)hits[0]!["location"]!["channelId"]));
            Assert.Equal(MailMsg, ids.Open(IdKinds.MailMessage, (string?)hits[1]!["id"]));
            Assert.Equal(ChatMsg, ids.Open(IdKinds.ChatMessage, (string?)hits[2]!["id"]));
            Assert.Equal(ChatId, ids.Open(IdKinds.Chat, (string?)hits[2]!["location"]!["chatId"]));
            Assert.Equal(Msg, ids.Open(IdKinds.ChannelMessage, (string?)hits[3]!["id"]));
            Assert.Equal("Deutsch", (string?)Body(await c.SearchFiles(h.Account, Query()))["hits"]![0]!["location"]!["path"]);
        }

        [Fact]
        public async Task Sealed_ids_open_to_raw_ids_upstream_and_the_query_stays_in_the_body()
        {
            var h = Create();
            var c = Ctl(h);
            var team = T(h, IdKinds.Team, Team);
            var channel = T(h, IdKinds.Channel, Channel);
            var drive = T(h, IdKinds.Drive, Drive);
            var item = T(h, IdKinds.DriveItemOther, Item);

            await c.TeamFiles(h.Account, team, channel, "Deutsch/Prüfungen", 9999);
            Assert.IsType<FileStreamResult>(await c.Content(h.Account, drive, item));
            Assert.IsType<FileContentResult>(await c.Thumbnail(h.Account, drive, item));
            await c.DriveText(h.Account, drive, item, maxChars: 999999);
            await c.FileText(h.Account, T(h, IdKinds.DriveItem, OwnItem));
            await c.Search(h.Account, Query(team));

            var calls = h.Service.Calls.Select(x => x.PathAndQuery).ToList();
            Assert.Contains($"/api/teams/{Team}/files?top=200&channel_id={Channel}&path=Deutsch/Prüfungen", calls);
            Assert.Contains($"/api/drives/{Drive}/items/{Item}/content", calls);
            Assert.Contains($"/api/drives/{Drive}/items/{Item}/thumbnail?size=medium", calls);
            Assert.Contains(calls, p => p.StartsWith($"/api/drives/{Drive}/items/{Item}/text?max_chars=100000&max_rows=500"));
            Assert.Contains(calls, p => p.StartsWith($"/api/files/{OwnItem}/text?max_chars=20000"));

            var search = h.Service.Calls.Single(x => x.PathAndQuery == "/api/search");
            Assert.Equal("POST", search.Method);
            Assert.DoesNotContain(h.Service.Calls, x => x.PathAndQuery.Contains("Masterplan"));
            var body = JsonNode.Parse(search.Body!)!;
            Assert.Equal("Masterplan", (string?)body["q"]);
            Assert.Equal(Team, (string?)body["teamId"]);
            Assert.Equal(4, body["types"]!.AsArray().Count);
        }

        [Fact]
        public async Task Foreign_ids_answer_404_without_calling_the_service()
        {
            var h = Create();
            var c = Ctl(h);
            var foreign = h.Seal(IdKinds.Opaque, "x");
            var ownItem = T(h, IdKinds.DriveItem, Item);
            var team = T(h, IdKinds.Team, Team);
            var channel = T(h, IdKinds.Channel, Channel);
            var drive = T(h, IdKinds.Drive, Drive);
            var item = T(h, IdKinds.DriveItemOther, Item);
            var message = T(h, IdKinds.ChannelMessage, Msg);
            var results = new List<IActionResult>
            {
                await c.Channels(h.Account, foreign),
                await c.Channels(h.Account, Team),
                await c.TeamFiles(h.Account, foreign),
                await c.TeamFiles(h.Account, team, foreign),
                await c.TeamFiles(h.Account, team, T(h, IdKinds.Team, Team)),
                await c.Item(h.Account, foreign, item),
                await c.Item(h.Account, drive, foreign),
                await c.Item(h.Account, drive, ownItem),
                await c.Item(h.Account, drive, Item),
                await c.Item(h.Account, T(h, IdKinds.Opaque, Drive), item),
                await c.Children(h.Account, drive, ownItem),
                await c.Content(h.Account, drive, ownItem),
                await c.Pdf(h.Account, drive, ownItem),
                await c.Thumbnail(h.Account, drive, ownItem),
                await c.DriveText(h.Account, foreign, item),
                await c.FileText(h.Account, item),
                await c.Messages(h.Account, foreign, channel),
                await c.Messages(h.Account, team, foreign),
                await c.Replies(h.Account, team, channel, foreign),
                await c.Replies(h.Account, team, channel, T(h, IdKinds.ChatMessage, Msg)),
                await c.Search(h.Account, Query(foreign)),
                await c.SearchFiles(h.Account, Query(foreign)),
            };
            Assert.All(results, r => Assert.IsType<NotFoundResult>(r));
            Assert.Empty(h.Service.Calls);
            Assert.NotEmpty(message);
        }

        [Theory]
        [InlineData("wrong user")]
        [InlineData("wrong account")]
        public async Task Another_users_or_accounts_ids_answer_404(string case_)
        {
            var h = Create();
            var otherUser = Guid.NewGuid();
            var otherUsersAccount = h.Accounts.Add(otherUser);
            var team = T(h, IdKinds.Team, Team);
            var (asUser, account) = case_ == "wrong user" ? (otherUser, otherUsersAccount) : (h.User, h.OtherAccount);

            Assert.IsType<NotFoundResult>(await Ctl(h, asUser).Channels(account, team));
            Assert.IsType<NotFoundResult>(await Ctl(h, asUser).Search(account, Query(team)));
            Assert.Empty(h.Service.Calls);
        }

        [Fact]
        public async Task Bad_raw_ids_behind_a_valid_seal_answer_400()
        {
            var h = Create();
            var c = Ctl(h);
            Assert.IsType<BadRequestObjectResult>(await c.Channels(h.Account, T(h, IdKinds.Team, "not-a-guid")));
            Assert.IsType<BadRequestObjectResult>(await c.Item(h.Account, T(h, IdKinds.Drive, "b!a/../b"), T(h, IdKinds.DriveItemOther, Item)));
            Assert.IsType<BadRequestObjectResult>(await c.Replies(h.Account, T(h, IdKinds.Team, Team), T(h, IdKinds.Channel, Channel), T(h, IdKinds.ChannelMessage, "abc")));
            Assert.IsType<BadRequestObjectResult>(await c.Search(h.Account, Query(T(h, IdKinds.Team, "nope"))));
            Assert.Empty(h.Service.Calls);
        }

        [Fact]
        public async Task Bad_paths_names_and_bodies_answer_400()
        {
            var h = Create();
            var c = Ctl(h);
            var team = T(h, IdKinds.Team, Team);
            Assert.IsType<BadRequestObjectResult>(await c.TeamFiles(h.Account, team, null, "a/../b"));
            Assert.IsType<BadRequestObjectResult>(await c.TeamFiles(h.Account, team, null, "a:b"));
            Assert.IsType<BadRequestObjectResult>(await c.Search(h.Account, new SearchRequest("  ", null, null, null, null)));
            Assert.IsType<BadRequestObjectResult>(await c.Search(h.Account, null));
            Assert.IsType<BadRequestObjectResult>(await c.Search(h.Account, new SearchRequest("x", ["sites"], null, null, null)));
            Assert.IsType<BadRequestObjectResult>(await c.Thumbnail(h.Account, T(h, IdKinds.Drive, Drive), T(h, IdKinds.DriveItemOther, Item), "huge"));
            Assert.Empty(h.Service.Calls);
        }
    }
}
