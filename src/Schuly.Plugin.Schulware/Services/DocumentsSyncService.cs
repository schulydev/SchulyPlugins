using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Schuly.Domain;
using Schuly.Infrastructure.Storage;
using Schuly.Plugin.Schulware.Client;
using Schuly.Plugin.Schulware.Client.Models;
using Schuly.Plugin.Schulware.Data;

namespace Schuly.Plugin.Schulware.Services
{
    /// <summary>
    /// Syncs a Schulware account's documents — including report cards (Zeugnisse,
    /// <c>Category == "Zeugnis"</c>) — into the main Schuly DB. Documents are
    /// scraper-only (no Mobile endpoint), so this requires a captured web session
    /// (PHPSESSID + id + transid). For each new file the raw bytes are pulled from
    /// Schulnetz immediately (the download link's transid is short-lived) and
    /// stored in the same S3 blob store as uploads, so the standard
    /// GET /api/documents/{id} download works. Dedup is on (SchoolUserId, Title,
    /// FileName).
    /// </summary>
    public class DocumentsSyncService(Schuly.Infrastructure.SchulyDbContext mainDb, IDocumentStorage storage, IHttpClientFactory httpClientFactory, ILogger<DocumentsSyncService> logger)
    {
        public async Task SyncAsync(SchulwareApiClient client, SchulwareAccount account, CancellationToken ct)
        {
            // No web session → documents can't be scraped. Silently skip; the
            // account still gets grades/agenda/absences via the Mobile API.
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
                // Schulnetz binds PHPSESSID to the UA that created it — replay it.
                UserAgent = account.UserAgent,
            }, cancellationToken: ct);

            var files = result?.Documents?.Files;
            if (files is null || files.Count == 0) return;

            var synced = 0;
            foreach (var file in files)
            {
                var title = file.Title ?? file.Filename;
                if (string.IsNullOrWhiteSpace(title)) continue;

                var exists = await mainDb.StudentDocuments.AnyAsync(
                    d => d.SchoolUserId == schoolUserId
                         && d.Title == title
                         && d.FileName == file.Filename, ct);
                if (exists) continue;

                // Pull the bytes now (the download link's transid expires quickly)
                // and stash them in S3 so the standard download endpoint serves them.
                string? fileUrl = null;
                long? fileSize = null;
                if (!string.IsNullOrWhiteSpace(file.DownloadUrl))
                {
                    var blob = await DownloadAndStoreAsync(account, file, ct);
                    if (blob is not null)
                    {
                        fileUrl = blob.Key;
                        fileSize = blob.SizeBytes;
                    }
                }

                mainDb.StudentDocuments.Add(new StudentDocument
                {
                    SchoolUserId = schoolUserId,
                    Title = title,
                    Comment = file.Comment,
                    Category = file.Category,
                    EnteredBy = file.CreatedBy,
                    FileName = file.Filename,
                    FileUrl = fileUrl,
                    FileSizeBytes = fileSize,
                });
                synced++;
            }

            if (synced > 0)
            {
                await mainDb.SaveChangesAsync(ct);
                logger.LogInformation("Synced {Count} documents for account {AccountId}", synced, account.Id);
            }
        }

        /// <summary>
        /// Fetch a document's bytes through SchulwareAPI's /websession/download
        /// (raw HTTP so we don't need to regenerate the typed client) and store
        /// them in the blob store. Returns null on any failure — the caller then
        /// persists the metadata without a downloadable file.
        /// </summary>
        private async Task<UploadedBlob?> DownloadAndStoreAsync(SchulwareAccount account, DocumentFileDto file, CancellationToken ct)
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

                // Copy to a seekable buffer; the S3 client needs a length.
                using var buffer = new MemoryStream();
                await resp.Content.CopyToAsync(buffer, ct);
                buffer.Position = 0;

                var contentType = resp.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
                var fileName = string.IsNullOrWhiteSpace(file.Filename) ? "document" : file.Filename;
                return await storage.UploadAsync(buffer, fileName, contentType, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Document download/store failed for {File}", file.Filename);
                return null;
            }
        }
    }
}
