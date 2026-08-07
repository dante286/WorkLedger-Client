namespace WorkLedger_Client.Models
{
    public interface IVersionedEntity
    {
        int Version { get; set; }
        string? OriginDeviceId { get; set; }
    }
}
