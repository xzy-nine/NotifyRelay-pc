using NotifyRelay.Data.Models;

namespace NotifyRelay.Data.Contracts;

public interface IDiscoveryService
{
    /// <summary>
    /// The list of discovered devices.
    /// </summary>
    ObservableCollection<DiscoveredDevice> DiscoveredDevices { get; }

    /// <summary>
    /// Starts the discovery process.
    /// </summary>
    Task StartDiscoveryAsync();

    void StopDiscovery();
}
