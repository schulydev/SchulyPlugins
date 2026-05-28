namespace Schuly.Plugin.OdaOrg.Data
{
    public class SyncState
    {
        public Guid Id { get; set; }
        public Guid AccountId { get; set; }
        public OdaOrgAccount? Account { get; set; }
        public DateTime? LastSyncAt { get; set; }
        public string? LastSyncStatus { get; set; }
        public string? LastSyncError { get; set; }
    }
}
