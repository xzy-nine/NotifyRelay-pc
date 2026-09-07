using NotifyRelay.Data.Contracts;
using NotifyRelay.Data.Models;

namespace NotifyRelay.Services;

public class HeartbeatProcessor
{
    private readonly ILogger _logger;
    private readonly IDeviceManager _deviceManager;

    public event Action<string, string?, string, ushort, string>? MdnsDeviceDiscovered;

    public HeartbeatProcessor(
        ILogger logger,
        IDeviceManager deviceManager)
    {
        _logger = logger;
        _deviceManager = deviceManager;
    }

    public void HandleMdnsDiscovered(string uuid, string? name, string ip, ushort port, int battery, string deviceType)
    {
        MdnsDeviceDiscovered?.Invoke(uuid, name, ip, port, deviceType);
    }

    private void MarkDeviceAlive(PairedDevice device)
    {
        device.LastHeartbeat = DateTime.UtcNow;
        if (!device.ConnectionStatus)
        {
            App.MainWindow?.DispatcherQueue?.TryEnqueue(() =>
            {
                device.ConnectionStatus = true;
            });
        }
    }
}


