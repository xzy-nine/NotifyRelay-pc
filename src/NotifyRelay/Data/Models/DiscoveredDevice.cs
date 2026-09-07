using NotifyRelay.Data.Enums;

namespace NotifyRelay.Data.Models;

public class DiscoveredDevice(
    string deviceId,
    string? publicKey,
    string deviceName,
    DateTimeOffset lastSeen,
    DeviceOrigin origin,
    int port,
    string ip = "",
    int battery = -101,
    string deviceType = "",
    bool isOnline = false,
    bool isPaired = false)
{
    public string DeviceId { get; } = deviceId;
    public string? PublicKey { get; } = publicKey;
    public string DeviceName { get; } = deviceName;
    public DateTimeOffset LastSeen { get; } = lastSeen;
    public DeviceOrigin Origin { get; } = origin;
    public int Port { get; } = port;
    public string Ip { get; } = ip;
    /// <summary>电量：正=充电中，负=放电中，-101=未知</summary>
    public int Battery { get; } = battery;
    public string DeviceType { get; } = deviceType;
    /// <summary>在线状态由 Rust core 判定</summary>
    public bool IsOnline { get; } = isOnline;
    /// <summary>配对状态由 Rust core 判定（存在共享密钥）</summary>
    public bool IsPaired { get; } = isPaired;
}
