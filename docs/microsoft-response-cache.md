# Microsoft response cache

The Microsoft plugin holds recent answers from SchulyMicrosoft in memory, so the web app's mail and chats
open quickly and use fewer of Microsoft's rate limits. This follows OutNote's
[frontend and API guide](https://github.com/PianoNic/OutNote/blob/main/docs/frontend-and-api.md). The plugin
is a C# port of the guide's cache, because the plugin owns the sign-in and refresh tokens, and SchulyMicrosoft
stays stateless.

```
Browser (IndexedDB) -> Plugin (memory) -> SchulyMicrosoft -> Microsoft
```

It's on for everyone and needs no setting. Nothing goes to disk or into a database, and a restart clears it.

## Routes

The web app reads through these. They answer with the same JSON and sealed ids as the live routes.

| Route under `/api/plugins/microsoft` | Answers |
|---|---|
| `GET accounts/{id}/cached/bootstrap` | `{mail, folders, chats}`: the first screen in one request |
| `GET accounts/{id}/cached/mail` | The list, with `folder`, `top`, `skip`, `unread` |
| `GET accounts/{id}/cached/mail/folders` | The folders |
| `GET accounts/{id}/cached/mail/{messageId}` | One message |
| `GET accounts/{id}/cached/chats` | The chat list, with `top` |
| `GET accounts/{id}/cached/chats/{chatId}/messages` | A chat's messages, with `top` |
| `DELETE cached` | Forgets the signed-in user's entries. The web app calls it before signing out, because plugins get no sign-out event. |

Each answer has an `ETag`, `Cache-Control: private, no-cache`, and `Age`. A matching `If-None-Match` gets a 304.

Every other route stays live. That includes the live routes these mirror, which is what the mobile app, Helply,
MCP, and connected AI apps call. These routes answer 404 to an MCP token.

## How old an answer can be

| Age | What happens |
|---|---|
| Under `Fresh` | Answers from memory |
| Under `Stale` | Answers from memory, and refreshes in the background |
| Older | Waits for Microsoft. If Microsoft is throttling or failing, answers the old result |
| Past `Stale` plus `KeepOldSeconds` | Gone, including as a fallback |

A message that was deleted, or a sign-in that expired, shows as such instead of as old data.

Writes always go to Microsoft first. Then the plugin forgets what they could have changed.

## Settings

Set these under `MicrosoftApi:Cache` in the plugin's configuration. Times are in seconds.

| Times (`Times:<name>:Fresh`, `Times:<name>:Stale`) | `Fresh` | `Stale` |
|---|---|---|
| `Bootstrap` | 5 | 600 |
| `Lists`: mail and chat lists | 30 | 600 |
| `ChatMessages`: an open chat | 10 | 600 |
| `Message`: one mail | 300 | 3600 |
| `Folders` | 300 | 86400 |

| Setting | Default | What it does |
|---|---|---|
| `KeepOldSeconds` | 3600 | How long past `Stale` an answer stays as a fallback |
| `MaxEntries` | 5000 | Most entries, for all users. The least recently used goes first |
| `MaxMegabytes` | 256 | Most memory for all answers |
| `CallsPerMinute` | 120 | Calls to SchulyMicrosoft per user through these routes. Past it, the user gets what's held, or a 429 with `Retry-After` |

## What's held, and for how long

Entries hold real mail and chats, unencrypted in the server's memory, keyed by Schuly user and linked account.

| Data | Longest time in memory, with the default settings |
|---|---|
| Mail and chat lists, a chat's messages | 70 minutes |
| One mail, with its body | 2 hours |
| Folder names and counts | 25 hours |

An entry goes sooner when the user signs out, unlinks the account, or deletes their Schuly account. An account
unlinked through `DELETE /api/me/linked-accounts/{id}`, which doesn't reach the plugin, is answered 404 at once and
leaves memory within 5 minutes. Someone whose session just expires can't call the sign-out route, so their entries
stay until these times run out.

The cache lives in one process, so it assumes one server, like the rest of the host's plugin runtime. With several
servers, each keeps its own copy and a write forgets only that server's copy.
