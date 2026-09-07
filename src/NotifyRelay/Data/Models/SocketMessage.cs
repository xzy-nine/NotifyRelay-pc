using NotifyRelay.Data.Enums;

namespace NotifyRelay.Data.Models;

public class SocketMessage
{
    [JsonPropertyName("type")]
    public virtual string Type { get; set; } = "DATA_JSON";
}



/// <summary>
/// 命令消息类
/// 路径: NotifyRelay.Data.Models.CommandMessage
/// 功能: 用于发送设备控制命令，如清除通知、请求应用列表、断开连接等
/// 调用位置: MainPageViewModel.cs, DevicesViewModel.cs
/// 处理服务: NotifyRelay.Services.MessageHandler.HandleMessageAsync
/// </summary>
public class CommandMessage : SocketMessage
{
    [JsonPropertyName("commandType")]
    public required CommandType CommandType { get; set; }
}

/// <summary>
/// 动作消息类
/// 路径: NotifyRelay.Data.Models.ActionMessage
/// 功能: 用于执行设备上的预定义动作，如锁屏、关机等
/// 处理服务: NotifyRelay.Services.MessageHandler.HandleMessageAsync → ActionService.HandleActionMessage
/// </summary>
public class ActionMessage : SocketMessage
{
    [JsonPropertyName("actionId")]
    public required string ActionId { get; set; }

    [JsonPropertyName("actionName")]
    public required string ActionName { get; set; }
}

/// <summary>
/// 自定义动作消息类
/// 路径: NotifyRelay.Data.Models.CustomActionMessage
/// 功能: 用于执行自定义路径的程序，支持传递参数
/// 处理服务: NotifyRelay.Services.MessageHandler.HandleMessageAsync → ActionService.HandleActionMessage
/// </summary>
public class CustomActionMessage : SocketMessage
{
    [JsonPropertyName("path")]
    public required string Path { get; set; }

    [JsonPropertyName("arguments")]
    public string? Arguments { get; set; } = null;
}



/// <summary>
/// 设备信息类
/// 路径: NotifyRelay.Data.Models.DeviceInfo
/// 功能: 包含设备的基本信息，如设备ID、名称、型号、公钥等
/// 处理服务: NotifyRelay.Services.MessageHandler.HandleMessageAsync
/// </summary>
public class DeviceInfo : SocketMessage
{
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("avatar")]
    public string? Avatar { get; set; }

    [JsonPropertyName("nonce")]
    public string? Nonce { get; set; }

    [JsonPropertyName("proof")]
    public string? Proof { get; set; }

    [JsonPropertyName("publicKey")]
    public string PublicKey { get; set; } = string.Empty;
}

/// <summary>
/// 设备状态类
/// 路径: NotifyRelay.Data.Models.DeviceStatus
/// 功能: 包含设备的实时状态信息，如电量、充电状态、WiFi状态等
/// 处理服务: NotifyRelay.Services.MessageHandler.HandleMessageAsync → DeviceManager.UpdateDeviceStatus
/// </summary>
public class DeviceStatus : SocketMessage
{
    [JsonPropertyName("batteryStatus")]
    public int BatteryStatus { get; set; }

    [JsonPropertyName("chargingStatus")]
    public bool ChargingStatus { get; set; }

    [JsonPropertyName("wifiStatus")]
    public bool WifiStatus { get; set; }

    [JsonPropertyName("bluetoothStatus")]
    public bool BluetoothStatus { get; set; }

    [JsonPropertyName("isDndEnabled")]
    public bool IsDndEnabled { get; set; }

    [JsonPropertyName("ringerMode")]
    public int RingerMode { get; set; }
}



/// <summary>
/// 音频设备类
/// 路径: NotifyRelay.Data.Models.AudioDevice
/// 功能: 包含音频设备的详细信息，如设备ID、名称、音量、静音状态等
/// 处理服务: NotifyRelay.Services.MessageHandler.HandleMessageAsync
/// </summary>
public class AudioDevice : SocketMessage
{
    [JsonPropertyName("audioDeviceType")]
    public AudioMessageType AudioDeviceType { get; set; }

    [JsonPropertyName("deviceId")]
    public required string DeviceId { get; set; }

    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = string.Empty;

    [JsonPropertyName("volume")]
    public float Volume { get; set; }

    [JsonPropertyName("isMuted")]
    public bool IsMuted { get; set; }

    [JsonPropertyName("isSelected")]
    public bool IsSelected { get; set; }
}

/// <summary>
/// 文件传输类
/// 路径: NotifyRelay.Data.Models.FileTransfer
/// 功能: 用于在设备间传输单个文件，包含文件元数据和服务器信息
/// 处理服务: NotifyRelay.Services.MessageHandler.HandleMessageAsync → FileTransferService.ReceiveFile
/// </summary>
public class FileTransfer : SocketMessage
{
    [JsonPropertyName("transferType")]
    public FileTransferType TransferType { get; set; }

    [JsonPropertyName("fileMetadata")]
    public required FileMetadata FileMetadata { get; set; }

    [JsonPropertyName("serverInfo")]
    public required ServerInfo ServerInfo { get; set; }
}

/// <summary>
/// 批量文件传输类
/// 路径: NotifyRelay.Data.Models.BulkFileTransfer
/// 功能: 用于在设备间批量传输文件，包含多个文件的元数据和服务器信息
/// 处理服务: NotifyRelay.Services.MessageHandler.HandleMessageAsync → FileTransferService.ReceiveBulkFiles
/// </summary>
public class BulkFileTransfer : SocketMessage
{
    [JsonPropertyName("files")]
    public required List<FileMetadata> Files { get; set; }

    [JsonPropertyName("serverInfo")]
    public required ServerInfo ServerInfo { get; set; }
}

/// <summary>
/// 服务器信息类
/// 路径: NotifyRelay.Data.Models.ServerInfo
/// 功能: 包含文件传输服务器的连接信息，如IP地址、端口、密码等
/// 用于: FileTransfer, BulkFileTransfer 类中
/// </summary>
public class ServerInfo
{
    [JsonPropertyName("ipAddress")]
    public string IpAddress { get; set; } = string.Empty;

    [JsonPropertyName("port")]
    public required int Port { get; set; }

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// 文件元数据类
/// 路径: NotifyRelay.Data.Models.FileMetadata
/// 功能: 包含文件的基本信息，如文件名、MIME类型、文件大小等
/// 用于: FileTransfer, BulkFileTransfer 类中
/// </summary>
public class FileMetadata
{
    [JsonPropertyName("fileName")]
    public required string FileName { get; set; }

    [JsonPropertyName("mimeType")]
    public required string MimeType { get; set; }

    [JsonPropertyName("fileSize")]
    public required long FileSize { get; set; }
}

/// <summary>
/// 设备铃声模式类
/// 路径: NotifyRelay.Data.Models.DeviceRingerMode
/// 功能: 包含设备的铃声模式信息
/// 处理服务: NotifyRelay.Services.MessageHandler.HandleMessageAsync
/// </summary>
public class DeviceRingerMode : SocketMessage
{
    [JsonPropertyName("ringerMode")]
    public int RingerMode { get; set; }
}