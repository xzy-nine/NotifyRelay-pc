using System.Net.NetworkInformation;
using System.Text;
using CommunityToolkit.WinUI;
using NotifyRelay.Data.Contracts;
using NotifyRelay.Data.Models;
using NotifyRelay.Native;
using Windows.UI.Notifications;


namespace NotifyRelay.Services;

public class NetworkService(
    ILogger<NetworkService> logger,
    IDeviceManager deviceManager,
    IAdbService adbService,
    ISystemInfoService systemInfoService,
    IProtocolSender protocolSender,
    Func<IRemoteAppService> remoteAppServiceFactory) : INetworkService, ISessionManager
{
    public int ServerPort { get; private set; } = 23333;
    private bool isRunning;

    private string? localPublicKey;
    private string? localDeviceId;
    private string? localDeviceName;
    private readonly Lazy<IRemoteAppService> remoteAppService = new(remoteAppServiceFactory);

    private ObservableCollection<PairedDevice> PairedDevices => deviceManager.PairedDevices;

    /// <summary>
    /// Event fired when a device connection status changes
    /// </summary>
    public event EventHandler<(PairedDevice Device, bool IsConnected)>? ConnectionStatusChanged;

    public async Task<bool> StartServerAsync()
    {
        if (isRunning)
        {
            logger.LogWarning("服务器已在运行");
            return false;
        }
        try
        {
            var localDevice = await deviceManager.GetLocalDeviceAsync();
            localPublicKey = NativeCore.GetPublicKey() ?? string.Empty;
            localDeviceId = localDevice.DeviceId;
            localDeviceName = localDevice.DeviceName;

            // 使用 Rust 统一启动接口（TCP、心跳调度、离线检测、发送队列、已知设备扫描、重连）
            var battery = systemInfoService.GetSystemBatteryLevel();
            var isCharging = systemInfoService.GetSystemChargingStatus();
            var signedBattery = isCharging ? Math.Abs(battery) : -Math.Abs(battery);
            var senderQueue = NativeCore.StartCore(
                localDeviceId ?? "", localDevice.DeviceName, signedBattery, "pc",
                (ushort)ServerPort, NativeCore.GetPublicKey() ?? string.Empty);
            if (senderQueue > 0)
            {
                isRunning = true;
                logger.LogInformation($"服务器已在端口 {ServerPort} 启动（Rust 统一启动完成）");

                // 登记已配对设备到 known_devices（心跳调度器与自动扫描依赖此列表）
                foreach (var paired in PairedDevices.ToList())
                {
                    // 跳过本机自身记录（历史残留或配对异常写入），避免自我连接循环
                    if (paired.Id == localDeviceId) continue;
                    // 优先用最近一次握手更新的 IP，其次用最新追加的 IP，避免旧 IP 残留导致心跳连错目标
                    var ip = paired.RemoteIpAddress ?? paired.IpAddresses?.LastOrDefault();
                    if (!string.IsNullOrEmpty(ip))
                    {
                        NativeCore.AddKnownDevice(paired.Id, ip);
                    }
                }
                logger.LogInformation("步骤17-StartServer: 已登记 {count} 个已配对设备", PairedDevices.Count);

                // 订阅电量变化监听（实时推送本机电量/充电状态）
                systemInfoService.BatteryChanged += OnBatteryChanged;
                logger.LogInformation("步骤17-StartServer: 电量变化监听已注册");

                // 注册网络变化监听
                logger.LogInformation("步骤17-StartServer: 注册网络变化监听");
                NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
                logger.LogInformation("步骤17-StartServer: 网络监听注册完成");

                return true;
            }

            logger.LogError("启动服务器失败");
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError("启动服务器时发生错误：{ex}", ex);
            return false;
        }
    }

    public void SendMessage(string deviceId, string message)
    {
        _ = protocolSender.SendMessageAsync(deviceId, message);
    }

    public void BroadcastMessage(string message)
    {
        try
        {
            var targets = PairedDevices.Where(d => d.ConnectionStatus).Select(d => d.Id).ToList();
            foreach (var deviceId in targets)
            {
                SendMessage(deviceId, message);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "向所有设备发送消息时出错");
        }
    }

    public void DisconnectDevice(string deviceId)
    {
        NativeCore.RemoveDeviceSession(deviceId);
    }

    public async Task HandleHandshakeAsync(string remoteDeviceId, string remotePublicKey, string remoteIpAddress, int battery, string remoteDeviceType, bool autoAccept = false)
    {
        logger.LogInformation($"收到握手来自 {remoteIpAddress} (类型: {remoteDeviceType}, auto_accept: {autoAccept})");

        // 检查是否是已知设备，如果是已知设备（重连），则不自动请求应用列表
        bool isKnownDevice = PairedDevices.Any(d => d.Id == remoteDeviceId);

        var device = await deviceManager.VerifyHandshakeAsync(remoteDeviceId, remotePublicKey, null, remoteIpAddress);

        if (device is not null)
        {
            logger.LogInformation($"设备 {device.Id} 已连接");

            device = await deviceManager.UpdateOrAddDeviceAsync(device, connectedDevice =>
            {
                connectedDevice.ConnectionStatus = true;
                connectedDevice.RemotePublicKey = remotePublicKey;
                connectedDevice.RemoteIpAddress = remoteIpAddress;
                connectedDevice.RemoteDeviceType = remoteDeviceType;
                deviceManager.ActiveDevice = connectedDevice;
                connectedDevice.LastHeartbeat = DateTime.UtcNow;

                if (connectedDevice.DeviceSettings.AdbAutoConnect && !string.IsNullOrEmpty(remoteIpAddress))
                {
                    adbService.TryConnectTcp(remoteIpAddress);
                }
            });

            // Rust 侧已对已配对设备自动发送 ACCEPT（auto_accept=true），
            // 平台侧无需重复发送，仅保留 UI 持久化与 known_device 登记
            if (!autoAccept)
            {
                if (localDeviceId is not null && localPublicKey is not null)
                {
                    var localBattery = systemInfoService.GetSystemBatteryLevel();
                    var localIp = NativeCore.GetLocalIp() ?? string.Empty;
                    NativeCore.SendAccept(remoteDeviceId, localPublicKey, localIp, localBattery, "pc");
                }
            }

            var nonNullDevice = device!;
            ConnectionStatusChanged?.Invoke(this, (nonNullDevice, true));

            // 登记到 known_devices，供心跳调度器与自动扫描驱动
            // 跳过本机自身记录，避免自我连接循环
            if (!string.IsNullOrEmpty(remoteIpAddress) && device.Id != localDeviceId)
            {
                NativeCore.AddKnownDevice(device.Id, remoteIpAddress);
            }

            // 延迟请求应用列表，避免阻塞握手
            // 仅对新配对设备自动触发；auto_accept 场景已由 Rust 延迟 3s 自动发送
            if (!isKnownDevice && !autoAccept)
            {
                DelayedRequestAppList(device.Id);
            }
        }
        else
        {
            // Rust 已对该设备自动 ACCEPT（auto_accept=true），
            // 平台库缺失仅影响持久化，不应再补发 REJECT 造成对端状态冲突
            if (!autoAccept)
            {
                NativeCore.SendReject(remoteDeviceId);
                logger.LogInformation("设备验证失败或被拒绝");
            }
            else
            {
                logger.LogWarning($"设备不在配对库但已由 Rust 自动接受(auto_accept)，跳过 REJECT: {remoteDeviceId}");
            }
        }
    }

    /// <summary>
    /// 处理 PAIRING_INIT：接收端（PC）收到发起端（Android）的配对请求。
    /// 协议格式：PAIRING_INIT:<uuid>:<tmpPubKey>:<ipAddress>:<batteryLevel>:<deviceType>
    /// 流程：弹出配对码输入对话框 → 用发起端临时公钥加密配对码 → 回传 PAIRING_RESP
    /// </summary>
    public async Task HandlePairingInitAsync(string remoteUuid, string tmpPubKey, string remoteIp, int battery, string deviceType)
    {
        logger.LogInformation("HandlePairingInitAsync 进入: uuid={uuid}, ip={ip}", remoteUuid, remoteIp);
        try
        {
            // 已配对设备：先删除旧记录，允许重新配对刷新密钥
            var existingDevice = deviceManager.PairedDevices.FirstOrDefault(d => d.Id == remoteUuid);
            if (existingDevice != null)
            {
                logger.LogWarning("设备已配对，重新配对刷新密钥: {uuid}", remoteUuid);
                deviceManager.RemoveDevice(existingDevice);
            }

            logger.LogInformation($"收到 PAIRING_INIT: {remoteUuid}");

            // 在主线程上显示配对码输入对话框
            string? pairingCode = null;
            // 尝试从已发现设备中获取设备名
            var discoveredName = PairedDevices.FirstOrDefault(d => d.Id == remoteUuid)?.Name
                ?? Ioc.Default.GetService<IDiscoveryService>()?.DiscoveredDevices
                    .FirstOrDefault(d => d.DeviceId == remoteUuid)?.DeviceName
                ?? remoteUuid;
            await App.MainWindow.DispatcherQueue.EnqueueAsync(async () =>
            {
                var dialog = new Dialogs.PairingCodeDialog(discoveredName)
                {
                    XamlRoot = App.MainWindow.Content!.XamlRoot
                };
                var result = await dialog.ShowAsync(ContentDialogPlacement.Popup);
                if (result == ContentDialogResult.Primary)
                {
                    pairingCode = dialog.PairingCode;
                }
            });

            // 用户取消了输入
            if (pairingCode == null)
            {
                logger.LogInformation($"用户取消了配对: {remoteUuid}");
                NativeCore.SendReject(remoteUuid);
                return;
            }

            // 使用 Rust 核心发送加密的 PAIRING_RESP（内部完成临时密钥生成+配对码加密）
            try
            {
                var ltPubKey = NativeCore.GetPublicKey();
                if (string.IsNullOrEmpty(ltPubKey))
                {
                    var localDevice = await deviceManager.GetLocalDeviceAsync();
                    ltPubKey = Encoding.UTF8.GetString(localDevice.PublicKey ?? Array.Empty<byte>());
                }
                NativeCore.SendPairingResp(localDeviceId ?? string.Empty, ltPubKey, pairingCode, remoteIp, systemInfoService.GetSystemBatteryLevel(), "pc");
                logger.LogInformation($"已发送 PAIRING_RESP: {remoteUuid}");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "发送 PAIRING_RESP 失败: {uuid}", remoteUuid);
                NativeCore.SendReject(remoteUuid);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理 PAIRING_INIT 异常");
        }
    }

    /// <summary>
    /// 处理 PAIRING_RESP：PC 作为发起端时收到接收端回传的加密配对码。
    /// 协议格式：PAIRING_RESP:<uuid_R>:<tmpPub_R>:<ltPub_R>:<encryptedCode>:<ip>:<battery>:<deviceType>
    /// 注意：当前 PC 通常作为接收端，此方法主要用于未来扩展或 PC-PC 配对。
    /// </summary>
    public async Task HandlePairingRespAsync(string remoteUuid, string spake2Pub, string ltPub, string ip, int battery, string deviceType)
    {
        try
        {
            logger.LogWarning("收到 PAIRING_RESP，但 PC 当前不作为配对发起端。忽略: {uuid}", remoteUuid);
            NativeCore.SendReject(remoteUuid);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理 PAIRING_RESP 异常");
        }
        await Task.CompletedTask;
    }

    /// <summary>
    /// 处理 ACCEPT（由 Rust on_accept 回调调用，结构化参数，无需 JSON 解析）
    /// </summary>
    public async Task HandlePairingAcceptAsync(string remoteUuid, string remoteLtPubKey, string remoteIp, int battery, string remoteDeviceType)
    {
        try
        {
            logger.LogInformation($"收到 ACCEPT，配对码验证通过: {remoteUuid}");

            // 与安卓端及 core HANDSHAKE 重连逻辑对齐：用对端长期公钥做 ECDH 派生，覆盖 SPAKE2 协商密钥，
            // 否则安卓端（ECDH 密钥）与 PC 端（SPAKE2 密钥）不一致，DATA 消息无法互相解密。
            // 派生失败为致命错误：不得静默视为配对成功（否则会以空/错误密钥登记设备，
            // 后续 DATA 无法解密且两端密钥不一致难以排查）。失败时直接返回，不登记、不标记已配对。
            if (string.IsNullOrEmpty(remoteLtPubKey) || NativeCore.DeriveSharedSecret(remoteUuid, remoteLtPubKey) != 0)
            {
                logger.LogError("配对完成后 ECDH 会话密钥派生失败（致命），取消配对: {uuid}", remoteUuid);
                return;
            }

            var existing = PairedDevices.FirstOrDefault(d => d.Id == remoteUuid);
            if (existing != null)
            {
                var keyJson = NativeCore.ExportDeviceKey(remoteUuid);
                byte[]? aesKey = null;
                if (keyJson != null)
                {
                    try { var aesB64 = System.Text.Json.JsonDocument.Parse(keyJson).RootElement.GetProperty("aes_key_b64").GetString(); if (!string.IsNullOrEmpty(aesB64)) aesKey = Convert.FromBase64String(aesB64); } catch { }
                }
                // 本方法运行在 Rust 回调线程，UI 绑定对象的修改必须调度到 UI 线程，否则会触发
                // XAML CollectionChanged 原生处理器抛出 COMException(0x80004005)
                UpdateDeviceState(existing, d =>
                {
                    if (aesKey != null) d.SharedSecret = aesKey;
                    d.RemotePublicKey = remoteLtPubKey;
                    if (!(d.IpAddresses ??= []).Contains(remoteIp)) d.IpAddresses.Add(remoteIp);
                    d.RemoteDeviceType = remoteDeviceType;
                    deviceManager.ActiveDevice = d;
                    ConnectionStatusChanged?.Invoke(this, (d, true));
                });
                // 登记到 known_devices，供心跳调度器与自动扫描驱动
                NativeCore.AddKnownDevice(remoteUuid, remoteIp);
                logger.LogInformation($"配对完成（更新已有设备）: {remoteUuid}");
            }
            else
            {
                var keyJson = NativeCore.ExportDeviceKey(remoteUuid);
                byte[]? sharedSecret = null;
                if (keyJson != null)
                {
                    try { var aesB64 = System.Text.Json.JsonDocument.Parse(keyJson).RootElement.GetProperty("aes_key_b64").GetString(); if (!string.IsNullOrEmpty(aesB64)) sharedSecret = Convert.FromBase64String(aesB64); } catch { }
                }
                var newDevice = new PairedDevice(remoteUuid)
                {
                    Name = remoteUuid,
                    RemotePublicKey = remoteLtPubKey,
                    IpAddresses = [remoteIp],
                    RemoteDeviceType = remoteDeviceType,
                    SharedSecret = sharedSecret,
                };
                deviceManager.SaveDevice(newDevice);
                // 使用线程安全的 UpdateOrAddDeviceAsync：内部已在 UI 线程去重添加，
                // 避免 ACCEPT 与随后握手(VerifyHandshakeAsync)并发重复 Add 导致 ListView 的
                // CollectionChanged 原生处理器抛出 COMException(0x80004005)
                await deviceManager.UpdateOrAddDeviceAsync(newDevice, d =>
                {
                    d.ConnectionStatus = true;
                    deviceManager.ActiveDevice = d;
                    ConnectionStatusChanged?.Invoke(this, (d, true));
                });
                // 登记到 known_devices，供心跳调度器与自动扫描驱动
                NativeCore.AddKnownDevice(remoteUuid, remoteIp);
                logger.LogInformation($"新设备配对完成: {remoteUuid}");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理 ACCEPT 异常");
        }
    }

    /// <summary>
    /// 处理 REJECT（由 Rust on_reject 回调调用）
    /// </summary>
    public async Task HandleRejectAsync(string remoteUuid)
    {
        try
        {
            logger.LogWarning($"收到 REJECT: {remoteUuid}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理 REJECT 异常");
        }
        await Task.CompletedTask;
    }

    /// <summary>
    /// 处理配对结果回调（由 Rust on_pairing_result 回调调用，统一配对成功/失败通知）
    /// </summary>
    public async Task HandlePairingResultAsync(string remoteUuid, int success, string errorMsg)
    {
        try
        {
            if (success == 1)
            {
                logger.LogInformation($"配对成功: {remoteUuid}");
            }
            else
            {
                logger.LogWarning($"配对失败: {remoteUuid}, error={errorMsg}");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理配对结果异常");
        }
        await Task.CompletedTask;
    }

    private void DelayedRequestAppList(string deviceId)
    {
        Task.Run(async () =>
        {
            try
            {
                // 等待几秒钟，确保连接稳定，且不阻塞主握手流程
                await Task.Delay(3000);

                remoteAppService.Value.SendAppListRequest(deviceId);
                logger.LogDebug("已自动触发设备 {deviceId} 的应用列表请求", deviceId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "自动请求应用列表失败");
            }
        });
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        logger.LogDebug("网络地址变化，通知 Rust core");
        var localIp = NativeCore.GetLocalIp();
        NativeCore.OnNetworkChanged(localIp);
    }

    /// <summary>
    /// 本机电量/充电状态变化：更新调度器广播参数（名称/电量/设备类型）
    /// </summary>
    private void OnBatteryChanged(object? sender, EventArgs e)
    {
        try
        {
            var battery = systemInfoService.GetSystemBatteryLevel();
            var isCharging = systemInfoService.GetSystemChargingStatus();
            var signedBattery = isCharging ? Math.Abs(battery) : -Math.Abs(battery);
            NativeCore.UpdateHeartbeatSchedulerParams(localDeviceName ?? "", signedBattery, "pc");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理电量变化失败");
        }
    }

    private void UpdateDeviceState(PairedDevice device, Action<PairedDevice> update)
    {
        var dispatcher = App.MainWindow?.DispatcherQueue;

        if (dispatcher is null)
        {
            update(device);
            return;
        }

        if (dispatcher.HasThreadAccess)
        {
            update(device);
            return;
        }

        dispatcher.TryEnqueue(() => update(device));
    }

    /// <summary>
    /// 显示非阻塞系统通知，提示设备需要升级
    /// </summary>
    private static void ShowUpgradeToast(string deviceName)
    {
        try
        {
            var template = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
            var elements = template.GetElementsByTagName("text");
            elements[0].AppendChild(template.CreateTextNode("设备协议不兼容"));
            elements[1].AppendChild(template.CreateTextNode($"设备「{deviceName}」使用旧版加密协议，已被拒绝连接。请升级该设备上的 NotifyRelay。"));
            var toast = new ToastNotification(template);
            ToastNotificationManager.CreateToastNotifier().Show(toast);
        }
        catch
        {
            // Toast 通知可能因系统权限或上下文不可用而失败，静默忽略
        }
    }
}
