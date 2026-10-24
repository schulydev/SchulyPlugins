using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Client;
using Schuly.Plugin.Schulware.Client.Models;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Shared;

namespace Schuly.Plugin.Schulware.Services
{
    /// <summary>
    /// Syncs a Schulware account's documents, including report cards (Zeugnisse,
    /// <c>Category == "Zeugnis"</c>). Documents are scraper-only, so this needs a web
    /// session. A new file's bytes are pulled immediately (the download link's transid is
    /// short-lived) and stored through <see cref="IPluginFileStore"/>.
    /// </summary>
    public class DocumentsSyncService(ISchoolData schoolData, IPluginFileStore files, IHttpClientFactory httpClientFactory, ILogger<DocumentsSyncService> logger)
    {
        public async Task SyncAsync(SchulwareApiClient client, SchulwareAccount account, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(account.WebSessionId)
                || string.IsNullOrEmpty(account.WebSessionUserId)
                || string.IsNullOrEmpty(account.WebSessionTransId))
                return;

            var schoolUserId = account.SchoolUserId!.Value;

            var result = await client.Api.Websession.Scrape.PostAsync(new WebScrapeRequestDto
            {
                Page = "documents",
                SessionId = account.WebSessionId,
                Id = account.WebSessionUserId,
                Transid = account.WebSessionTransId,
                // Schulnetz binds PHPSESSID to the UA that created it.
                UserAgent = account.UserAgent,
            }, cancellationToken: ct);

            var scraped = result?.Documents?.Files;
            if (scraped is null || scraped.Count == 0) return;

            var known = await schoolData.GetDocumentIdsAsync(schoolUserId, ct);
            var records = new List<DocumentRecord>();
            foreach (var file in scraped)
            {
                var title = file.Title ?? file.Filename;
                if (string.IsNullOrWhiteSpace(title)) continue;

                var id = ExternalIds.Of("document", title, file.Filename);
                if (known.Contains(id)) continue;

                var stored = string.IsNullOrWhiteSpace(file.DownloadUrl) ? null : await DownloadAndStoreAsync(account, file, ct);
                records.Add(new DocumentRecord(id, title, file.Filename, file.Category, file.Comment, file.CreatedBy, stored));
            }

            if (records.Count == 0) return;
            var synced = await schoolData.SyncDocumentsAsync(schoolUserId, records, ct);
            logger.LogInformation("Synced {Count} documents for account {AccountId}", synced.Added, account.Id);
        }

        private async Task<StoredFile?> DownloadAndStoreAsync(SchulwareAccount account, DocumentFileDto file, CancellationToken ct)
        {
            try
            {
                var http = httpClientFactory.CreateClient();
                using var req = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"{account.SchulwareApiBaseUrl.TrimEnd('/')}/api/websession/download");
                req.Headers.Add("X-Schulnetz-Base-Url", account.SchulnetzBaseUrl);
                req.Content = JsonContent.Create(new
                {
                    session_id = account.WebSessionId,
                    download_url = file.DownloadUrl,
                    user_agent = account.UserAgent,
                });

                using var resp = await http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    logger.LogWarning("Document download failed ({Status}) for {File}",
                        resp.StatusCode, file.Filename);
                    return null;
                }

                using var buffer = new MemoryStream();
                await resp.Content.CopyToAsync(buffer, ct);
                buffer.Position = 0;

                var contentType = resp.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
                var fileName = string.IsNullOrWhiteSpace(file.Filename) ? "document" : file.Filename;
                return await files.SaveAsync(buffer, fileName, contentType, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Document download/store failed for {File}", file.Filename);
                return null;
            }
        }
    }
}
