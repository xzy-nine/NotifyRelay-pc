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
    private readonly DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();
    private LocalDeviceEntity? localDevice;
    private bool isInitialized = false;

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
            logger.LogInformation("Rust mDNS 服务由统一启动接口（nrc_start_core）管理");

            // 通过 Rust 内核启动周期性设备广播
            NativeCore.PeriodicBroadcast(1, localDevice.DeviceId, localDevice.DeviceName, signedBattery, "pc");

            // 订阅 mDNS 发现事件
            heartbeatProcessor.MdnsDeviceDiscovered += OnMdnsDeviceDiscovered;

            isInitialized = true;
            logger.LogInformation("发现服务已完全初始化");
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
            // 更新心跳调度器广播信息与 mDNS 广告（Rust 端重建广告使新名称生效）
            NativeCore.UpdateHeartbeatSchedulerParams(newName, signedBattery, "pc");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理本地设备名更改时出错");
        }
    }

    private async void OnMdnsDeviceDiscovered(string uuid, string? name, string ip, ushort port, string deviceType)
    {
        if (uuid == localDevice?.DeviceId) return;

        logger.LogInformation("mDNS 发现设备：{deviceId}，{deviceName}，{ip}", uuid, name, ip);

        await dispatcher.EnqueueAsync(() =>
        {
            if (!isInitialized) return;

            var discovered = new DiscoveredDevice(
                uuid, ip, name ?? "unknown",
                DateTimeOffset.UtcNow, DeviceOrigin.MdnsService, port);

            var existing = DiscoveredDevices.FirstOrDefault(d => d.DeviceId == uuid);
            if (existing is not null)
            {
                var index = DiscoveredDevices.IndexOf(existing);
                DiscoveredDevices[index] = discovered;
            }
            else
            {
                DiscoveredDevices.Add(discovered);
            }
        });
    }

    public void StopDiscovery()
    {
        NativeCore.PeriodicBroadcast(0);
        heartbeatProcessor.MdnsDeviceDiscovered -= OnMdnsDeviceDiscovered;

        try
        {
            // mDNS 广告/发现的停止统一交由核心关闭流程（nrc_start_core 统一管理），此处不再主动调用
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
}
