using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.WinUI;
using Microsoft.UI.Dispatching;
using NotifyRelay.Data.AppDatabase.Models;
using NotifyRelay.Data.Contracts;
using NotifyRelay.Data.Enums;
using NotifyRelay.Data.Models;
using NotifyRelay.Native;

namespace NotifyRelay.Services;

public class DiscoveryService(
    ILogger logger,
    IDeviceManager deviceManager,
    HeartbeatProcessor heartbeatProcessor
    ) : IDiscoveryService
{
    /// <summary>
    /// 设备快照兜底刷新间隔（与 Android 端保持一致）：
    /// 保证设备离线后即使没有回调也能从列表中移除
    /// </summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);

    private readonly DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();
    private LocalDeviceEntity? localDevice;
    private bool isInitialized = false;
    private bool refreshBusy = false;
    private DispatcherQueueTimer? refreshTimer;

    public ObservableCollection<DiscoveredDevice> DiscoveredDevices { get; } = [];

    public async Task StartDiscoveryAsync()
    {
        try
        {
            await dispatcher.EnqueueAsync(() =>
            {
                DiscoveredDevices.Clear();
                logger.LogInformation("设备列表已清理");
            });

            localDevice = await deviceManager.GetLocalDeviceAsync();
            logger.LogInformation("本地设备初始化完成：{deviceId}, {deviceName}", localDevice.DeviceId, localDevice.DeviceName);

            deviceManager.LocalDeviceNameChanged += OnLocalDeviceNameChanged;
            logger.LogInformation("事件处理程序已设置");

            var systemInfoService = Ioc.Default.GetService<ISystemInfoService>();
            var batteryLevel = systemInfoService?.GetSystemBatteryLevel() ?? 100;
            var isCharging = systemInfoService?.GetSystemChargingStatus() ?? true;
            var signedBattery = isCharging ? Math.Abs(batteryLevel) : -Math.Abs(batteryLevel);

            // 通过 Rust 内核启动周期性 TCP 扫描发现
            NativeCore.PeriodicBroadcast(1, localDevice.DeviceId, localDevice.DeviceName, signedBattery, "pc");

            // 设备状态变化（扫描发现 / 设备超时）→ 重新拉取 core 快照刷新列表
            heartbeatProcessor.DeviceListChanged += OnDeviceListChanged;

            isInitialized = true;
            logger.LogInformation("发现服务已完全初始化");

            await RefreshFromCoreAsync();
            StartPeriodicRefresh();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "启动发现服务时出错");
            isInitialized = false;
            await dispatcher.EnqueueAsync(() =>
            {
                DiscoveredDevices.Clear();
            });
        }
    }

    private void OnLocalDeviceNameChanged(object? sender, string newName)
    {
        try
        {
            if (localDevice == null) return;
            logger.LogInformation("本地设备名已更改：{newName}", newName);
            localDevice.DeviceName = newName;
            NativeCore.PeriodicBroadcast(2, name: newName);

            var systemInfoService = Ioc.Default.GetService<ISystemInfoService>();
            var batteryLevel = systemInfoService?.GetSystemBatteryLevel() ?? 100;
            var isCharging = systemInfoService?.GetSystemChargingStatus() ?? true;
            var signedBattery = isCharging ? Math.Abs(batteryLevel) : -Math.Abs(batteryLevel);
            NativeCore.UpdateHeartbeatSchedulerParams(newName, signedBattery, "pc");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理本地设备名更改时出错");
        }
    }

    private void OnDeviceListChanged()
    {
        // 回调来自 Rust 线程：刷新内部自行切回 UI 线程
        _ = RefreshFromCoreAsync();
    }

    private void StartPeriodicRefresh()
    {
        refreshTimer ??= dispatcher.CreateTimer();
        refreshTimer.Interval = RefreshInterval;
        refreshTimer.Tick += OnRefreshTimerTick;
        refreshTimer.Start();
    }

    private void OnRefreshTimerTick(object? sender, object e)
    {
        _ = RefreshFromCoreAsync();
    }

    /// <summary>
    /// 从 Rust core 拉取设备状态快照并重建列表。
    /// 在线/离线、可见性、名称/IP/电量全部由 core 判定，平台端只负责展示。
    /// </summary>
    private async Task RefreshFromCoreAsync()
    {
        if (!isInitialized || refreshBusy) return;
        refreshBusy = true;
        try
        {
            var json = NativeCore.GetDeviceList();
            if (string.IsNullOrEmpty(json)) return;

            var snapshot = JsonSerializer.Deserialize<List<DeviceSnapshot>>(json);
            if (snapshot is null) return;

            await dispatcher.EnqueueAsync(() =>
            {
                if (!isInitialized) return;

                var visible = new HashSet<string>();
                foreach (var d in snapshot)
                {
                    if (string.IsNullOrEmpty(d.Uuid) || d.Uuid == localDevice?.DeviceId) continue;
                    visible.Add(d.Uuid);

                    var lastSeen = d.LastSeen > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(d.LastSeen)
                        : DateTimeOffset.UtcNow;
                    var discovered = new DiscoveredDevice(
                        d.Uuid,
                        null,
                        string.IsNullOrEmpty(d.Name) ? d.Uuid : d.Name,
                        lastSeen,
                        DeviceOrigin.TcpScan,
                        d.Port,
                        d.Ip ?? string.Empty,
                        d.Battery,
                        d.DeviceType ?? string.Empty,
                        d.Online,
                        d.Paired);

                    var existing = DiscoveredDevices.FirstOrDefault(x => x.DeviceId == d.Uuid);
                    if (existing is not null)
                    {
                        var index = DiscoveredDevices.IndexOf(existing);
                        DiscoveredDevices[index] = discovered;
                    }
                    else
                    {
                        DiscoveredDevices.Add(discovered);
                    }
                }

                // core 判定不再可见的设备（离线且未配对）从列表移除
                for (var i = DiscoveredDevices.Count - 1; i >= 0; i--)
                {
                    if (!visible.Contains(DiscoveredDevices[i].DeviceId))
                    {
                        DiscoveredDevices.RemoveAt(i);
                    }
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "刷新 core 设备快照时出错");
        }
        finally
        {
            refreshBusy = false;
        }
    }

    public void StopDiscovery()
    {
        NativeCore.PeriodicBroadcast(0);
        heartbeatProcessor.DeviceListChanged -= OnDeviceListChanged;

        if (refreshTimer is not null)
        {
            refreshTimer.Tick -= OnRefreshTimerTick;
            refreshTimer.Stop();
        }

        try
        {
            deviceManager.LocalDeviceNameChanged -= OnLocalDeviceNameChanged;
            dispatcher.TryEnqueue(() =>
            {
                DiscoveredDevices.Clear();
                isInitialized = false;
            });
        }
        catch (Exception ex)
        {
            logger.LogError("停止发现服务时出错：{message}", ex.Message);
        }
    }

    /// <summary>
    /// Rust core 设备状态快照条目（字段与 nrc_get_device_list 输出一致）
    /// </summary>
    private sealed class DeviceSnapshot
    {
        [JsonPropertyName("uuid")] public string? Uuid { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("ip")] public string? Ip { get; set; }
        [JsonPropertyName("port")] public int Port { get; set; }
        [JsonPropertyName("battery")] public int Battery { get; set; }
        [JsonPropertyName("deviceType")] public string? DeviceType { get; set; }
        [JsonPropertyName("lastSeen")] public long LastSeen { get; set; }
        [JsonPropertyName("online")] public bool Online { get; set; }
        [JsonPropertyName("paired")] public bool Paired { get; set; }
    }
}
