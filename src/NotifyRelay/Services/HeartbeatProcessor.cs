using NotifyRelay.Data.Contracts;
using NotifyRelay.Data.Models;

namespace NotifyRelay.Services;

public class HeartbeatProcessor
{
    private readonly ILogger _logger;
    private readonly IDeviceManager _deviceManager;

    /// <summary>
    /// 设备状态变化通知（由 Rust 回调触发，如 TCP 扫描发现、设备超时）。
    /// 设备状态（在线/离线/名称/IP/电量/是否可见）完全由 Rust core 负责，
    /// 平台端不解析具体字段，仅据此重新拉取 core 设备快照后刷新 UI。
    /// </summary>
    public event Action? DeviceListChanged;

    public HeartbeatProcessor(
        ILogger logger,
        IDeviceManager deviceManager)
    {
        _logger = logger;
        _deviceManager = deviceManager;
    }

    /// <summary>
    /// 触发设备列表刷新（运行在 Rust 回调线程，订阅方需自行切回 UI 线程）
    /// </summary>
    public void NotifyDeviceListChanged()
    {
        DeviceListChanged?.Invoke();
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


