namespace PosFormatador;

public class HardwareDevice
{
    public string Name { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string HardwareId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public uint ErrorCode { get; set; }
    public bool HasDriverIssue => ErrorCode != 0;
    public string StatusDisplay => HasDriverIssue ? $"Sem driver / Erro {ErrorCode}" : "OK / Instalado";
}
