using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Schuly.Plugin.Microsoft.Controllers;
using Schuly.Plugin.Microsoft.Infrastructure;
using Xunit;

namespace Schuly.Plugin.Microsoft.Tests
{
    /// <summary>The plugin's routes against a fake SchulyMicrosoft: ids are sealed on the way out (nested ones too), opened on the way in, and refused (404) for the wrong user, account or kind or when tampered with.</summary>
    public class SealedRoutesTests
    {
        private const string Chat = "19:rawchat0001@thread.v2";
        private const string SelfChat = "48:notes";
        private const string Msg = "1700000000001";
        private const string Msg2 = "1700000000002";
        private const string Reply = "1699999999999";
        private const string Me = "11111111-aaaa-bbbb-cccc-000000000001";
        private const string User2 = "11111111-aaaa-bbbb-cccc-000000000002";
        private const string Tenant = "22222222-aaaa-bbbb-cccc-000000000003";
        private const string Membership = "MCMjMSMjrawmembership01";
        private const string FileAtt = "rawfileattachment01";
        private const string Hosted = "aWQ9cmF3aG9zdGVkMDE=";
        private const string EmojiDoc = "rawcustomemojidoc01";
        private const string MailMsg = "AAMkRAWMAILMESSAGE01=";
        private const string Folder = "AAMkRAWFOLDER01=";
        private const string RootFolder = "AAMkRAWROOTFOLDER00=";
        private const string ChildFolder = "AAMkRAWCHILDFOLDER02=";
        private const string Conversation = "AAQkRAWCONVERSATION01=";
        private const string InternetId = "<rawinternet01@mail.school.ch>";
        private const string MailAtt = "AAMkRAWATTACHMENT01=";
        private const string DriveItem = "01RAWDRIVEITEM01";
        private const string DriveParent = "01RAWDRIVEPARENT02";
        private const string DriveId = "b!RAWDRIVEID01";
        private const string Event = "AAMkRAWEVENT01=";
        private const string SeriesMaster = "AAMkRAWSERIES01=";
        private const string ICal = "040000008200E00074C5B7101A82E008RAWICAL";
        private const string Notebook = "0-RAWNOTEBOOK01!1";
        private const string TeamGroup = "33333333-aaaa-bbbb-cccc-000000000004";
        private const string Section = "0-RAWSECTION01!2";
        private const string Section2 = "0-RAWSECTION02!6";
        private const string Group = "0-RAWGROUP01!3";
        private const string Page = "0-RAWPAGE01!4";
        private const string Resource = "0-RAWRESOURCE01!5";

        private static readonly string[] RawIds = [Chat, SelfChat, Msg, Msg2, Reply, Me, User2, Tenant, Membership, FileAtt, Hosted, EmojiDoc, MailMsg, Folder, RootFolder, ChildFolder, Conversation, InternetId, MailAtt, DriveItem, DriveParent, DriveId, Event, SeriesMaster, ICal, Notebook, TeamGroup, Section, Section2, Group, Page, Resource];

        // Links clients open in Microsoft's own apps: they carry Microsoft's ids by design and no route accepts them back.
        private static readonly string[] LinkFields = ["webUrl", "webLink", "joinUrl", "contentUrl", "@microsoft.graph.downloadUrl"];

        private static Harness Create()
        {
            var h = new Harness();
            var a = h.Service.Answers;
            a["me"] = J(new { id = Me, displayName = "Me", mail = "me@school.ch", @odata_context = "https://graph.microsoft.com/v1.0/$metadata#users/$entity" });
            a["chats"] = $$$$$"""
            [{"id":"{{{{{Chat}}}}}","topic":"Class","chatType":"group","tenantId":"{{{{{Tenant}}}}}","webUrl":"https://teams.microsoft.com/l/chat/19%3Arawchat0001%40thread.v2/0","@odata.etag":"W/1",
              "members":[{"@odata.type":"#microsoft.graph.aadUserConversationMember","id":"{{{{{Membership}}}}}","userId":"{{{{{Me}}}}}","displayName":"Me","email":"me@school.ch","tenantId":"{{{{{Tenant}}}}}"},
                         {"@odata.type":"#microsoft.graph.aadUserConversationMember","id":"{{{{{Membership}}}}}2","userId":"{{{{{User2}}}}}","displayName":"B","email":"b@school.ch","tenantId":"{{{{{Tenant}}}}}"}],
              "lastMessagePreview":{"id":"{{{{{Msg}}}}}","isDeleted":false,"body":{"contentType":"text","content":"hi"},"from":{"user":{"id":"{{{{{User2}}}}}","displayName":"B","userIdentityType":"aadUser","tenantId":"{{{{{Tenant}}}}}"}}},"hasImage":true},
             {"id":"{{{{{SelfChat}}}}}","topic":null,"chatType":"oneOnOne","members":[{"id":"{{{{{Membership}}}}}3","userId":"{{{{{Me}}}}}","displayName":"Me"}],"hasImage":false}]
            """;
            a[$"chats/{Chat}/messages"] = $$$$$"""
            [{"id":"{{{{{Msg}}}}}","replyToId":"{{{{{Reply}}}}}","chatId":"{{{{{Chat}}}}}","etag":"1","messageType":"message","@odata.context":"https://graph.microsoft.com/v1.0/$metadata#chats('{{{{{Chat}}}}}')/messages",
              "from":{"user":{"id":"{{{{{User2}}}}}","displayName":"B","userIdentityType":"aadUser","tenantId":"{{{{{Tenant}}}}}"}},
              "body":{"contentType":"html","content":"<p>hi <at id=\"0\">B</at><img src=\"https://graph.microsoft.com/v1.0/chats/{{{{{Chat}}}}}/messages/{{{{{Msg}}}}}/hostedContents/{{{{{Hosted}}}}}/$value\"></p>"},
              "attachments":[{"id":"{{{{{FileAtt}}}}}","contentType":"reference","contentUrl":"https://school.sharepoint.com/a.docx","name":"a.docx"}],
              "mentions":[{"id":0,"mentionText":"B","mentioned":{"user":{"id":"{{{{{User2}}}}}","displayName":"B","userIdentityType":"aadUser","tenantId":"{{{{{Tenant}}}}}"}}}],
              "reactions":[{"reactionType":"like","user":{"user":{"id":"{{{{{Me}}}}}","userIdentityType":"aadUser"}}}],
              "media":[{"kind":"hosted","url":null,"hostedContentId":"{{{{{Hosted}}}}}"}],
              "files":[{"id":"{{{{{FileAtt}}}}}","name":"a.docx","kind":"word","hasThumbnail":true,"webUrl":"https://school.sharepoint.com/a.docx"}],
              "emojis":[{"key":"e0","id":"laugh","kind":"teams","documentId":null},{"key":"e1","id":"party;{{{{{EmojiDoc}}}}}","kind":"custom","documentId":"{{{{{EmojiDoc}}}}}"}],
              "cards":[]},
             {"id":"{{{{{Msg2}}}}}","messageType":"systemEventMessage","body":{"content":"<systemEventMessage/>"},
              "eventDetail":{"@odata.type":"#microsoft.graph.membersAddedEventMessageDetail","chatId":"{{{{{Chat}}}}}","members":[{"id":"{{{{{User2}}}}}","displayName":null,"userIdentityType":"aadUser","tenantId":"{{{{{Tenant}}}}}"}],"initiator":{"user":{"id":"{{{{{Me}}}}}","userIdentityType":"aadUser","tenantId":"{{{{{Tenant}}}}}"}}}}]
            """;
            a["mail"] = $$$$$"""[{"id":"{{{{{MailMsg}}}}}","subject":"s","parentFolderId":"{{{{{Folder}}}}}","conversationId":"{{{{{Conversation}}}}}","internetMessageId":"{{{{{InternetId}}}}}","webLink":"https://outlook.office365.com/owa/?ItemID=AAMkRAWMAILMESSAGE01%3D","from":{"emailAddress":{"address":"a@b.ch","name":"A"}}}]""";
            a[$"mail/{MailMsg}"] = $$$$$"""{"id":"{{{{{MailMsg}}}}}","subject":"s","parentFolderId":"{{{{{Folder}}}}}","conversationId":"{{{{{Conversation}}}}}","body":{"contentType":"html","content":"<img src=\"cid:image001.png@01D\">"},"hasAttachments":true}""";
            a[$"mail/{MailMsg}/attachments"] = $$$$$"""[{"id":"{{{{{MailAtt}}}}}","name":"image001.png","contentType":"image/png","size":10,"isInline":true,"contentId":"image001.png@01D"}]""";
            a[$"mail/{MailMsg}/move"] = $$$$$"""{"id":"{{{{{MailMsg}}}}}","parentFolderId":"{{{{{ChildFolder}}}}}"}""";
            a["mail/folders"] = $$$$$"""[{"id":"{{{{{Folder}}}}}","displayName":"Inbox","parentFolderId":"{{{{{RootFolder}}}}}","wellKnownName":"inbox","unreadItemCount":1,"childFolders":[{"id":"{{{{{ChildFolder}}}}}","displayName":"Sub","parentFolderId":"{{{{{Folder}}}}}","childFolders":[]}]}]""";
            a["files"] = $$$$$"""[{"id":"{{{{{DriveItem}}}}}","name":"a.docx","size":10,"file":{"mimeType":"application/x"},"webUrl":"https://school-my.sharepoint.com/a.docx","@microsoft.graph.downloadUrl":"https://school-my.sharepoint.com/download.aspx?t=1","parentReference":{"driveType":"business","driveId":"{{{{{DriveId}}}}}","id":"{{{{{DriveParent}}}}}","path":"/drives/{{{{{DriveId}}}}}/root:"},"createdBy":{"user":{"id":"{{{{{User2}}}}}","displayName":"B"}}}]""";
            a["calendar"] = $$$$$"""[{"id":"{{{{{Event}}}}}","iCalUId":"{{{{{ICal}}}}}","seriesMasterId":"{{{{{SeriesMaster}}}}}","subject":"Lesson","webLink":"https://outlook.office365.com/owa/?itemid=x","onlineMeeting":{"joinUrl":"https://teams.microsoft.com/l/meetup-join/x"}}]""";
            a["onenote/notebooks"] = $$$$$"""[{"id":"{{{{{Notebook}}}}}","displayName":"N","writable":true,"source":{"kind":"team","id":"{{{{{TeamGroup}}}}}","name":"Class"},"webUrl":"https://school.sharepoint.com/n"}]""";
            a[$"onenote/notebooks/{Notebook}/sections"] = $$$$$"""{"notebookId":"{{{{{Notebook}}}}}","children":[{"id":"{{{{{Section}}}}}","kind":"section","displayName":"S","children":[]},{"id":"{{{{{Group}}}}}","kind":"group","displayName":"G","children":[{"id":"{{{{{Section2}}}}}","kind":"section","displayName":"S2","children":[]}]}]}""";
            a[$"onenote/sections/{Section}/pages"] = $$$$$"""{"items":[{"id":"{{{{{Page}}}}}","title":"P","level":0,"sectionId":"{{{{{Section}}}}}"}],"nextSkip":null}""";
            a[$"onenote/pages/{Page}"] = $$$$$"""{"id":"{{{{{Page}}}}}","title":"P","html":"<div data-id=\"div:{x}{1}\"><img data-resource=\"{{{{{Resource}}}}}\" alt=\"\"></div>","resources":[{"id":"{{{{{Resource}}}}}","kind":"image","name":null}],"outlines":[{"id":"div:{x}{1}","editable":true,"html":"x"}],"notebook":{"id":"{{{{{Notebook}}}}}","displayName":"N"},"section":{"id":"{{{{{Section}}}}}","displayName":"S"},"writable":true}""";
            a["onenote/search"] = $$$$$"""{"items":[{"id":"{{{{{Page}}}}}","title":"P","section":{"id":"{{{{{Section}}}}}","displayName":"S"},"notebook":{"id":"{{{{{Notebook}}}}}","displayName":"N"}}],"partial":false}""";
            a[$"users/{User2}/photo"] = "bytes:png";
            a[$"chats/{Chat}/messages/{Msg}/files/{FileAtt}/thumbnail"] = "bytes:png";
            return h;
        }

        private static string J(object o) => JsonSerializer.Serialize(o).Replace("\"odata_context\"", "\"@odata.context\"");

        private static AccountsController Accounts(Harness h, Guid? asUser = null) => h.Controller((u, l, r) => new AccountsController(u, l, r, h.Sealer, h.ResponseCache), asUser);
        private static MailController Mail(Harness h) => h.Controller((u, l, r) => new MailController(u, l, r, h.Sealer, h.ResponseCache));
        private static ChatsController Chats(Harness h) => h.Controller((u, l, r) => new ChatsController(u, l, r, h.Scheduled(), h.Sealer, h.ResponseCache));
        private static OneNoteController OneNote(Harness h) => h.Controller((u, l, r) => new OneNoteController(u, l, r, h.Sealer));

        private static JsonNode Body(IActionResult result)
        {
            var value = Assert.IsAssignableFrom<ObjectResult>(result).Value;
            return JsonNode.Parse(JsonSerializer.Serialize(value))!;
        }

        private static async Task<IReadOnlyList<(string Route, JsonNode Json)>> EveryJsonAnswer(Harness h)
        {
            var s = (string kind, string id) => h.Seal(kind, id);
            return
            [
                ("me", Body(await Accounts(h).Me(h.Account))),
                ("chats", Body(await Accounts(h).Chats(h.Account))),
                ("messages", Body(await Accounts(h).ChatMessages(h.Account, s(IdKinds.Chat, Chat)))),
                ("mail", Body(await Accounts(h).Mail(h.Account))),
                ("mail by folder", Body(await Accounts(h).Mail(h.Account, folder: s(IdKinds.MailFolder, Folder)))),
                ("mail message", Body(await Mail(h).Message(h.Account, s(IdKinds.MailMessage, MailMsg)))),
                ("mail attachments", Body(await Mail(h).Attachments(h.Account, s(IdKinds.MailMessage, MailMsg)))),
                ("mail move", Body(await Mail(h).Move(h.Account, s(IdKinds.MailMessage, MailMsg), new MailMoveRequest(s(IdKinds.MailFolder, ChildFolder))))),
                ("mail folders", Body(await Mail(h).Folders(h.Account))),
                ("files", Body(await Accounts(h).Files(h.Account))),
                ("calendar", Body(await Accounts(h).Calendar(h.Account))),
                ("notebooks", Body(await OneNote(h).Notebooks(h.Account))),
                ("sections", Body(await OneNote(h).Sections(h.Account, s(IdKinds.Notebook, Notebook)))),
                ("pages", Body(await OneNote(h).Pages(h.Account, s(IdKinds.Section, Section)))),
                ("page", Body(await OneNote(h).Page(h.Account, s(IdKinds.Page, Page)))),
                ("search", Body(await OneNote(h).Search(h.Account, "p", s(IdKinds.Notebook, Notebook)))),
            ];
        }

        [Fact]
        public async Task No_raw_graph_id_appears_in_any_json_answer()
        {
            var h = Create();
            foreach (var (route, json) in await EveryJsonAnswer(h))
            {
                StripLinks(json);
                var text = json.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                foreach (var raw in RawIds)
                    Assert.False(text.Contains(raw, StringComparison.Ordinal), $"{route} leaks {raw}: {text}");
                Assert.DoesNotContain("@odata.context", text);
                Assert.DoesNotContain("graph.microsoft.com", text);
            }
        }

        [Fact]
        public async Task Nested_ids_are_sealed_with_the_kind_their_route_takes()
        {
            var h = Create();
            var ids = h.Scope();
            var me = Body(await Accounts(h).Me(h.Account));
            var chats = Body(await Accounts(h).Chats(h.Account)).AsArray();
            var messages = Body(await Accounts(h).ChatMessages(h.Account, h.Seal(IdKinds.Chat, Chat))).AsArray();

            Assert.Equal(Me, ids.Open(IdKinds.User, (string?)me["id"]));
            Assert.Equal(Chat, ids.Open(IdKinds.Chat, (string?)chats[0]!["id"]));
            Assert.False((bool)chats[0]!["isSelfChat"]!);
            Assert.True((bool)chats[1]!["isSelfChat"]!);
            Assert.Equal(SelfChat, ids.Open(IdKinds.Chat, (string?)chats[1]!["id"]));
            // The user's own id is equal everywhere it appears, so clients can tell their own messages and members apart.
            Assert.Equal((string?)me["id"], (string?)chats[0]!["members"]![0]!["userId"]);
            Assert.Equal(User2, ids.Open(IdKinds.User, (string?)chats[0]!["members"]![1]!["userId"]));
            Assert.Equal(Membership, ids.Open(IdKinds.Opaque, (string?)chats[0]!["members"]![0]!["id"]));
            Assert.Equal(Tenant, ids.Open(IdKinds.Opaque, (string?)chats[0]!["tenantId"]));
            Assert.Equal(Msg, ids.Open(IdKinds.ChatMessage, (string?)chats[0]!["lastMessagePreview"]!["id"]));
            Assert.Equal(User2, ids.Open(IdKinds.User, (string?)chats[0]!["lastMessagePreview"]!["from"]!["user"]!["id"]));
            Assert.Equal("#microsoft.graph.aadUserConversationMember", (string?)chats[0]!["members"]![0]!["@odata.type"]);
            Assert.Null(chats[0]!["@odata.etag"]);

            var m = messages[0]!;
            Assert.Equal(Msg, ids.Open(IdKinds.ChatMessage, (string?)m["id"]));
            Assert.Equal(Reply, ids.Open(IdKinds.ChatMessage, (string?)m["replyToId"]));
            Assert.Equal(Chat, ids.Open(IdKinds.Chat, (string?)m["chatId"]));
            Assert.Equal(User2, ids.Open(IdKinds.User, (string?)m["mentions"]![0]!["mentioned"]!["user"]!["id"]));
            Assert.Equal(0, (int)m["mentions"]![0]!["id"]!);
            Assert.Equal((string?)me["id"], (string?)m["reactions"]![0]!["user"]!["user"]!["id"]);
            Assert.Equal(Hosted, ids.Open(IdKinds.HostedContent, (string?)m["media"]![0]!["hostedContentId"]));
            Assert.Equal(FileAtt, ids.Open(IdKinds.ChatFile, (string?)m["files"]![0]!["id"]));
            Assert.Equal((string?)m["files"]![0]!["id"], (string?)m["attachments"]![0]!["id"]);
            Assert.Equal("laugh", (string?)m["emojis"]![0]!["id"]);
            Assert.Equal(EmojiDoc, ids.Open(IdKinds.Emoji, (string?)m["emojis"]![1]!["documentId"]));
            Assert.DoesNotContain("graph.microsoft.com", (string?)m["body"]!["content"]);
            Assert.Contains("<at id=\"0\">B</at>", (string?)m["body"]!["content"]);

            var e = messages[1]!["eventDetail"]!;
            Assert.Equal("#microsoft.graph.membersAddedEventMessageDetail", (string?)e["@odata.type"]);
            Assert.Equal(User2, ids.Open(IdKinds.User, (string?)e["members"]![0]!["id"]));
            Assert.Equal((string?)me["id"], (string?)e["initiator"]!["user"]!["id"]);
        }

        [Fact]
        public async Task Mail_folders_drive_and_onenote_nest_their_ids()
        {
            var h = Create();
            var ids = h.Scope();
            var folders = Body(await Mail(h).Folders(h.Account)).AsArray();
            var mail = Body(await Accounts(h).Mail(h.Account)).AsArray();
            var attachments = Body(await Mail(h).Attachments(h.Account, h.Seal(IdKinds.MailMessage, MailMsg))).AsArray();
            var files = Body(await Accounts(h).Files(h.Account)).AsArray();
            var sections = Body(await OneNote(h).Sections(h.Account, h.Seal(IdKinds.Notebook, Notebook)));
            var page = Body(await OneNote(h).Page(h.Account, h.Seal(IdKinds.Page, Page)));

            Assert.Equal(ChildFolder, ids.Open(IdKinds.MailFolder, (string?)folders[0]!["childFolders"]![0]!["id"]));
            Assert.Equal((string?)folders[0]!["id"], (string?)folders[0]!["childFolders"]![0]!["parentFolderId"]);
            Assert.Equal((string?)folders[0]!["id"], (string?)mail[0]!["parentFolderId"]);
            Assert.Equal(Conversation, ids.Open(IdKinds.MailConversation, (string?)mail[0]!["conversationId"]));
            Assert.Equal(MailAtt, ids.Open(IdKinds.MailAttachment, (string?)attachments[0]!["id"]));
            Assert.Equal("image001.png@01D", (string?)attachments[0]!["contentId"]);

            Assert.Equal(DriveItem, ids.Open(IdKinds.DriveItem, (string?)files[0]!["id"]));
            Assert.Equal(DriveParent, ids.Open(IdKinds.DriveItem, (string?)files[0]!["parentReference"]!["id"]));
            Assert.Equal("/drive/root:", (string?)files[0]!["parentReference"]!["path"]);
            Assert.NotNull(files[0]!["@microsoft.graph.downloadUrl"]);

            Assert.Equal(Notebook, ids.Open(IdKinds.Notebook, (string?)sections["notebookId"]));
            Assert.Equal(Section, ids.Open(IdKinds.Section, (string?)sections["children"]![0]!["id"]));
            Assert.Equal(Group, ids.Open(IdKinds.SectionGroup, (string?)sections["children"]![1]!["id"]));
            Assert.Equal(Section2, ids.Open(IdKinds.Section, (string?)sections["children"]![1]!["children"]![0]!["id"]));

            var resource = (string?)page["resources"]![0]!["id"];
            Assert.Equal(Resource, ids.Open(IdKinds.Resource, resource));
            Assert.Contains($"data-resource=\"{resource}\"", (string?)page["html"]);
            Assert.Equal("div:{x}{1}", (string?)page["outlines"]![0]!["id"]);
            Assert.Equal(Section, ids.Open(IdKinds.Section, (string?)page["section"]!["id"]));
        }

        [Fact]
        public async Task Answers_are_deterministic()
        {
            var h = Create();
            var first = Body(await Accounts(h).Chats(h.Account)).ToJsonString();
            var second = Body(await Accounts(h).Chats(h.Account)).ToJsonString();
            Assert.Equal(first, second);
        }

        [Fact]
        public async Task A_sealed_id_opens_to_the_raw_id_upstream()
        {
            var h = Create();
            var result = await Accounts(h).ChatMessages(h.Account, h.Seal(IdKinds.Chat, Chat));
            Assert.IsType<OkObjectResult>(result);
            Assert.Contains(h.Service.Calls, c => c.PathAndQuery.StartsWith($"/api/chats/{Chat}/messages"));

            var photo = await Accounts(h).Photo(h.Account, h.Seal(IdKinds.User, User2));
            Assert.IsType<FileContentResult>(photo);
            Assert.Contains(h.Service.Calls, c => c.PathAndQuery.StartsWith($"/api/users/{User2}/photo"));

            var thumb = await Chats(h).ChatFileThumbnail(h.Account, h.Seal(IdKinds.Chat, Chat), h.Seal(IdKinds.ChatMessage, Msg), h.Seal(IdKinds.ChatFile, FileAtt));
            Assert.IsType<FileContentResult>(thumb);
        }

        public static TheoryData<string> Refusals => new() { "wrong user", "wrong account", "wrong kind", "tampered", "raw", "garbage" };

        [Theory]
        [MemberData(nameof(Refusals))]
        public async Task Refused_ids_answer_404_without_calling_the_service(string case_)
        {
            var h = Create();
            var otherUser = Guid.NewGuid();
            var otherUsersAccount = h.Accounts.Add(otherUser);
            var good = h.Seal(IdKinds.Chat, Chat);
            var (asUser, account, chatId) = case_ switch
            {
                // Another Schuly user presents an id sealed for this user, on their own linked account.
                "wrong user" => (otherUser, otherUsersAccount, good),
                "wrong account" => (h.User, h.OtherAccount, good),
                "wrong kind" => (h.User, h.Account, h.Seal(IdKinds.ChatMessage, Chat)),
                "tampered" => (h.User, h.Account, good[..^2] + (good[^2] == 'A' ? 'B' : 'A') + good[^1]),
                "raw" => (h.User, h.Account, Chat),
                _ => (h.User, h.Account, "AAAA" + new string('x', 60)),
            };

            var result = await Accounts(h, asUser).ChatMessages(account, chatId);

            Assert.IsType<NotFoundResult>(result);
            Assert.Empty(h.Service.Calls);
        }

        [Fact]
        public async Task Every_id_taking_route_refuses_a_foreign_id()
        {
            var h = Create();
            var foreign = h.Seal(IdKinds.Opaque, "x");
            var results = new List<IActionResult>
            {
                await Accounts(h).Photo(h.Account, foreign),
                await Accounts(h).MarkChatRead(h.Account, foreign),
                await Accounts(h).Mail(h.Account, folder: foreign),
                await Mail(h).Message(h.Account, foreign),
                await Mail(h).Attachment(h.Account, foreign, foreign),
                await Mail(h).Delete(h.Account, foreign),
                await Mail(h).Move(h.Account, h.Seal(IdKinds.MailMessage, MailMsg), new MailMoveRequest(foreign)),
                await Chats(h).ChatPhoto(h.Account, foreign),
                await Chats(h).ChatMedia(h.Account, foreign, foreign, foreign),
                await Chats(h).ChatFile(h.Account, foreign, foreign, foreign),
                await Chats(h).DriveThumbnail(h.Account, foreign),
                await Chats(h).CustomEmoji(h.Account, foreign),
                await Chats(h).CancelScheduled(h.Account, foreign),
                await OneNote(h).Sections(h.Account, foreign),
                await OneNote(h).Page(h.Account, foreign),
                await OneNote(h).DeletePage(h.Account, foreign),
                await OneNote(h).Resource(h.Account, foreign),
                await h.Controller((u, l, r) => new FilePdfController(u, l, r, h.Sealer)).DriveItem(h.Account, foreign),
                await h.Controller((u, l, r) => new FilePdfController(u, l, r, h.Sealer)).MailAttachment(h.Account, foreign, foreign),
            };
            Assert.All(results, r => Assert.IsType<NotFoundResult>(r));
            Assert.Empty(h.Service.Calls);
        }

        [Fact]
        public async Task Well_known_folder_names_pass_and_sealed_folders_open()
        {
            var h = Create();
            await Mail(h).Move(h.Account, h.Seal(IdKinds.MailMessage, MailMsg), new MailMoveRequest("archive"));
            await Mail(h).Move(h.Account, h.Seal(IdKinds.MailMessage, MailMsg), new MailMoveRequest(h.Seal(IdKinds.MailFolder, ChildFolder)));
            await Accounts(h).Mail(h.Account, folder: h.Seal(IdKinds.MailFolder, Folder));
            var moves = h.Service.Calls.Where(c => c.PathAndQuery.EndsWith("/move")).Select(c => c.Body!).ToList();
            Assert.Contains("\"archive\"", moves[0]);
            Assert.Contains(ChildFolder, JsonNode.Parse(moves[1])!["folder"]!.GetValue<string>());
            Assert.Contains(h.Service.Calls, c => c.PathAndQuery.Contains($"folder={Folder}"));
        }

        [Theory]
        [InlineData("items/01RAWDRIVEITEM01")]
        [InlineData("root/../items/x")]
        [InlineData("drives/b!x/root")]
        public async Task Drive_paths_are_names_never_ids(string path)
        {
            var h = Create();
            Assert.IsType<BadRequestObjectResult>(await Accounts(h).Files(h.Account, path));
            Assert.Empty(h.Service.Calls);
        }

        [Fact]
        public async Task Mentions_in_a_send_are_opened_before_they_go_upstream()
        {
            var h = Create();
            h.Service.Answers[$"chats/{Chat}/messages"] = $$$$$"""{"id":"{{{{{Msg}}}}}","chatId":"{{{{{Chat}}}}}"}""";
            var chats = Chats(h);
            var body = JsonSerializer.Serialize(new { content = "<at id=\"0\">B</at> hi", contentType = "html", mentions = new[] { new { id = 0, mentionText = "B", mentioned = new { user = new { id = h.Seal(IdKinds.User, User2), displayName = "B" } } } } });
            chats.HttpContext.Request.ContentType = "application/json";
            chats.HttpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));

            var result = await chats.Send(h.Account, h.Seal(IdKinds.Chat, Chat));

            var answer = Body(result);
            Assert.Equal(Msg, h.Scope().Open(IdKinds.ChatMessage, (string?)answer["id"]));
            var sent = JsonNode.Parse(h.Service.Calls.Single().Body!)!;
            Assert.Equal(User2, (string?)sent["mentions"]![0]!["mentioned"]!["user"]!["id"]);
        }

        [Fact]
        public async Task Scheduled_messages_answer_sealed_ids_and_cancel_by_them()
        {
            var h = Create();
            var store = h.Scheduled();
            var chats = h.Controller((u, l, r) => new ChatsController(u, l, r, store, h.Sealer, h.ResponseCache));
            var created = Body(await chats.Schedule(h.Account, h.Seal(IdKinds.Chat, Chat), new ScheduleMessageRequest("hi", "text", null, DateTimeOffset.UtcNow.AddHours(1))));
            Assert.Equal(Chat, h.Scope().Open(IdKinds.Chat, (string?)created["chatId"]));
            var id = (string)created["id"]!;
            Assert.IsType<NotFoundResult>(await chats.CancelScheduled(h.OtherAccount, id));
            Assert.IsType<NoContentResult>(await chats.CancelScheduled(h.Account, id));
        }

        private static void StripLinks(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var key in obj.Select(p => p.Key).ToList())
                    {
                        if (LinkFields.Contains(key))
                            obj.Remove(key);
                        else
                            StripLinks(obj[key]);
                    }
                    break;
                case JsonArray array:
                    foreach (var child in array)
                        StripLinks(child);
                    break;
            }
        }
    }
}
