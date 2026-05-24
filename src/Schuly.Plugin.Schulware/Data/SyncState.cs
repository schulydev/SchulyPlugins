namespace Schuly.Plugin.Schulware.Data
{
    public class SyncState
    {
        public Guid Id { get; set; }
        public Guid AccountId { get; set; }
        public SchulwareAccount? Account { get; set; }
        public DateTime? LastSyncAt { get; set; }
        public string? LastSyncStatus { get; set; }
        public string? LastSyncError { get; set; }
    }
}
