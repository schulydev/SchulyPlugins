using Microsoft.Extensions.Logging;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Client;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Shared;

namespace Schuly.Plugin.Schulware.Services
{
    /// <summary>
    /// Syncs a Schulware account's documents, including report cards (Zeugnisse,
    /// <c>Category == "Zeugnis"</c>), from Schulnetz's mobile API (<c>/me/filestore</c>),
    /// which returns each file inline as a data URI. New files go to <see cref="IPluginFileStore"/>.
    /// </summary>
    public class DocumentsSyncService(ISchoolData schoolData, IPluginFileStore files, ILogger<DocumentsSyncService> logger)
    {
        public async Task SyncAsync(SchulwareApiClient client, SchulwareAccount account, CancellationToken ct)
        {
            var entries = await client.Api.Mobile.Filestore.GetAsync(cancellationToken: ct);
            if (entries is null || entries.Count == 0) return;

            var schoolUserId = account.SchoolUserId!.Value;
            var known = await schoolData.GetDocumentIdsAsync(schoolUserId, ct);
            var records = new List<DocumentRecord>();
            foreach (var entry in entries)
            {
                var fileName = entry.File?.Name;
                var title = string.IsNullOrWhiteSpace(entry.Title) ? fileName : entry.Title;
                if (string.IsNullOrWhiteSpace(title)) continue;

                var id = ExternalIds.Of("document", title, fileName);
                if (known.Contains(id)) continue;

                records.Add(new DocumentRecord(id, title, fileName, entry.Category, entry.Comment, null, await StoreAsync(entry.File?.Content, fileName, ct)));
            }

            if (records.Count == 0) return;
            var synced = await schoolData.SyncDocumentsAsync(schoolUserId, records, ct);
            logger.LogInformation("Synced {Count} documents for account {AccountId}", synced.Added, account.Id);
        }

        private async Task<StoredFile?> StoreAsync(string? dataUri, string? fileName, CancellationToken ct)
        {
            if (!DataUri.TryDecode(dataUri, out var bytes, out var contentType))
                return null;
            try
            {
                using var content = new MemoryStream(bytes);
                return await files.SaveAsync(content, string.IsNullOrWhiteSpace(fileName) ? "document" : fileName, contentType, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Storing document {File} failed", fileName);
                return null;
            }
        }
    }
}
