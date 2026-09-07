using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using NotifyRelay.Data.Contracts;
using NotifyRelay.Data.Models;
using NotifyRelay.Services;

namespace NotifyRelay.Native;

public static class NativeCore
{
    private static IntPtr _ctx = IntPtr.Zero;
    private static bool _initialized = false;
    private static string? _gitHash;

    // 回调分发目标
    internal static ProtocolRouter? ProtocolRouter { get; set; }
    internal static IDeviceManager? DeviceManager { get; set; }
    internal static NetworkService? NetworkService { get; set; }
    internal static HeartbeatProcessor? HeartbeatProcessor { get; set; }

    // 媒体会话存在性查询委托（由 WindowsPlaybackService 注册）：返回 true=仍有活跃媒体会话，false=无
    // Rust 心跳查询回调（on_state_query）调用，运行在 Rust 心跳线程；无活跃会话时 Rust 移除媒体发送会话。
    public static Func<string, bool>? MediaSessionQueryHandler { get; set; }

    // 保持回调委托不被 GC 回收
    private static readonly List<Delegate> _callbackRefs = new();

    // 已上线的设备集合（仅用于控制"设备已连接"日志仅状态变化时打印；维持性心跳连接不上线下线不打印）
    private static readonly ConcurrentDictionary<string, byte> _deviceOnline = new();

    public static IntPtr Context => _ctx;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        var asmLocation = typeof(NotifyRelayCore).Assembly.Location;
        var checkDirs = new[] {
            AppContext.BaseDirectory,
            Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd('\\')),
            Path.GetDirectoryName(asmLocation)
        };

        foreach (var dir in checkDirs)
        {
            if (dir == null) continue;
            var dllPath = Path.Combine(dir, "notify_relay_core.dll");
            if (File.Exists(dllPath))
            {
                NativeLibrary.Load(dllPath);
                break;
            }
        }

        _ctx = NotifyRelayCore.nrc_init();
        _gitHash = GetGitHash();
    }

    public static string? GetGitHash()
    {
        var ptr = NotifyRelayCore.nrc_get_git_hash();
        if (ptr == IntPtr.Zero) return null;
        var result = Marshal.PtrToStringAnsi(ptr);
        NotifyRelayCore.nrc_free_string(ptr);
        return result;
    }

    public static int MigrateSharedSecret(string deviceUuid, byte[] aesKey)
    {
        return NotifyRelayCore.Safe.MigrateSharedSecret(_ctx, deviceUuid, aesKey);
    }

    public static int RemoveDevice(string deviceUuid)
    {
        return NotifyRelayCore.Safe.RemoveDevice(_ctx, deviceUuid);
    }

    // ======== New methods ========

    public static int GenerateKeypair()
    {
        return NotifyRelayCore.Safe.GenerateKeypair(_ctx);
    }

    public static string? GetPublicKey()
    {
        return NotifyRelayCore.Safe.GetPublicKey(_ctx);
    }

    public static int HasKeypair()
    {
        return NotifyRelayCore.Safe.HasKeypair(_ctx);
    }

    public static int DeriveSharedSecret(string deviceUuid, string peerPubKeyB64)
    {
        return NotifyRelayCore.Safe.DeriveSharedSecret(_ctx, deviceUuid, peerPubKeyB64);
    }

    public static string? ExportDeviceKey(string deviceUuid)
    {
        return NotifyRelayCore.Safe.ExportDeviceKey(_ctx, deviceUuid);
    }

    public static string? GetLocalUuid()
    {
        return NotifyRelayCore.Safe.GetLocalUuid(_ctx);
    }

    public static int RenameDevice(string deviceUuid, string name)
    {
        return NotifyRelayCore.Safe.RenameDevice(_ctx, deviceUuid, name);
    }

    public static int PeriodicBroadcast(int action, string? uuid = null, string? name = null, int battery = -1, string? deviceType = null)
    {
        return NotifyRelayCore.Safe.PeriodicBroadcast(_ctx, action, uuid, name, battery, deviceType);
    }

    public static void SendHandshake(string uuid, string pubKey, string localIp, string targetIp, int battery, string deviceType)
    {
        NotifyRelayCore.Safe.SendHandshake(_ctx, uuid, pubKey, localIp, targetIp, battery, deviceType);
    }

    public static void SendPairingInit(string localUuid, string targetUuid, string expectedCode, int battery, string deviceType)
    {
        NotifyRelayCore.Safe.SendPairingInit(_ctx, localUuid, targetUuid, expectedCode, battery, deviceType);
    }

    public static void SendPairingResp(string uuid, string ltPub, string pairingCode, string ip, int battery, string deviceType)
    {
        NotifyRelayCore.Safe.SendPairingResp(_ctx, uuid, ltPub, pairingCode, ip, battery, deviceType);
    }

    public static void SendAccept(string uuid, string ltPubKey, string ip, int battery, string deviceType)
    {
        NotifyRelayCore.Safe.SendAccept(_ctx, uuid, ltPubKey, ip, battery, deviceType);
    }

    public static void SendReject(string uuid)
    {
        NotifyRelayCore.Safe.SendReject(_ctx, uuid);
    }

    // ======== Pairing code management (Rust-generated) ========
    public static string? GeneratePairingCode(uint ttlSecs = 300)
    {
        return NotifyRelayCore.Safe.GeneratePairingCode(_ctx, ttlSecs);
    }

    public static void ClearPairingCode()
    {
        NotifyRelayCore.Safe.ClearPairingCode(_ctx);
    }

    // ======== Network layer wrappers ========

    public static long StartCore(string uuid, string name, int battery, string deviceType, ushort tcpPort, string pubKey, ulong heartbeatIntervalMs = 2000, long offlineTimeoutSec = 12, ulong offlineCheckIntervalMs = 5000, ulong reconnectIntervalSecs = 10, uint reconnectMaxRetries = 5)
    {
        _senderQueueHandle = NotifyRelayCore.Safe.StartCore(_ctx, uuid, name, battery, deviceType, tcpPort, pubKey, heartbeatIntervalMs, offlineTimeoutSec, offlineCheckIntervalMs, reconnectIntervalSecs, reconnectMaxRetries);
        return _senderQueueHandle;
    }

    public static int RemoveDeviceSession(string uuid)
    {
        return NotifyRelayCore.Safe.RemoveDeviceSession(_ctx, uuid);
    }

    // ======== New function wrappers ========

    public static string? ComputeDedupKey(string deviceUuid, string data)
    {
        return NotifyRelayCore.Safe.ComputeDedupKey(deviceUuid, data);
    }

    public static string? ComputeFeatureId(string superPkg, string paramV2Raw, string title, string text, string instanceId)
    {
        return NotifyRelayCore.Safe.ComputeFeatureId(superPkg, paramV2Raw, title, text, instanceId);
    }

    public static string? ExportState()
    {
        return NotifyRelayCore.Safe.ExportState(_ctx);
    }

    public static int ImportState(string json)
    {
        return NotifyRelayCore.Safe.ImportState(_ctx, json);
    }

    public static string? EncryptLocalState(string plaintext, string deviceUuid)
    {
        return NotifyRelayCore.Safe.EncryptLocalState(_ctx, plaintext, deviceUuid);
    }

    public static string? DecryptLocalState(string encryptedB64, string deviceUuid)
    {
        return NotifyRelayCore.Safe.DecryptLocalState(_ctx, encryptedB64, deviceUuid);
    }

    // ======== Callback-driven architecture ========

    private static PairedDevice? FindDevice(IntPtr uuidPtr)
    {
        var uuid = Marshal.PtrToStringUTF8(uuidPtr);
        return uuid != null ? DeviceManager?.FindDeviceById(uuid) : null;
    }

    private static string? PtrToString(IntPtr ptr) => Marshal.PtrToStringUTF8(ptr);

    public static void SetLogCallback(ILogger logger)
    {
        if (_gitHash != null)
        {
            logger.LogInformation("NotifyRelay Core loaded (git: {GitHash})", _gitHash);
            _gitHash = null;
        }
        NotifyRelayCore.OnLogCb cb = (level, messagePtr) =>
        {
            var msg = Marshal.PtrToStringUTF8(messagePtr);
            if (msg == null) return;
            var logLevel = level switch
            {
                1 => LogLevel.Error,
                2 => LogLevel.Warning,
                3 => LogLevel.Information,
                4 => LogLevel.Debug,
                5 => LogLevel.Trace,
                _ => LogLevel.Debug,
            };
            logger.Log(logLevel, "[Rust] {Msg}", msg);
        };
        var fp = Marshal.GetFunctionPointerForDelegate(cb);
        NotifyRelayCore.nrc_set_log_callback(fp);
        _callbackRefs.Add(cb);
    }

    public static void RegisterCallbacks()
    {
        if (_ctx == IntPtr.Zero) return;

        NotifyRelayCore.OnPairingCb onPairingCb = (uuidPtr, msgTypePtr, dataPtr, intValue, extraPtr, userData) =>
        {
            var uuid = Marshal.PtrToStringUTF8(uuidPtr);
            var msgType = Marshal.PtrToStringUTF8(msgTypePtr);
            var data = Marshal.PtrToStringUTF8(dataPtr);
            var extra = Marshal.PtrToStringUTF8(extraPtr);
            if (uuid == null || msgType == null) return;

            // HEARTBEAT_TCP 是维持性心跳（每设备周期连接），静默；其余为真实配对事件，仅状态变化时打印
            if (msgType != "HEARTBEAT_TCP")
            {
                System.Diagnostics.Debug.WriteLine($"[CoreCb] 收到配对事件: 类型={msgType}, 设备={uuid}");
            }

            switch (msgType)
            {
                case "HANDSHAKE":
                    {
                        if (data == null) return;
                        string pubKey = "", ip = "", deviceType = "unknown";
                        bool autoAccept = false;
                        try
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(data);
                            pubKey = doc.RootElement.GetProperty("pub_key").GetString() ?? "";
                            ip = doc.RootElement.GetProperty("ip").GetString() ?? "";
                            deviceType = doc.RootElement.GetProperty("device_type").GetString() ?? "unknown";
                            if (doc.RootElement.TryGetProperty("auto_accept", out var aa) && aa.ValueKind == System.Text.Json.JsonValueKind.True)
                                autoAccept = true;
                        }
                        catch { }
                        var ns = NetworkService;
                        if (ns == null) return;
                        _ = ns.HandleHandshakeAsync(uuid, pubKey, ip, intValue, deviceType, autoAccept);
                    }
                    break;
                case "PAIRING_INIT":
                    {
                        if (data == null) return;
                        string spake2Pub = "", ip = "", deviceType = "unknown";
                        try
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(data);
                            spake2Pub = doc.RootElement.GetProperty("spake2_pub").GetString() ?? "";
                            ip = doc.RootElement.GetProperty("ip").GetString() ?? "";
                            deviceType = doc.RootElement.GetProperty("device_type").GetString() ?? "unknown";
                        }
                        catch { }
                        var ns = NetworkService;
                        if (ns == null) return;
                        _ = ns.HandlePairingInitAsync(uuid, spake2Pub, ip, intValue, deviceType);
                    }
                    break;
                case "PAIRING_RESP":
                    {
                        if (data == null) return;
                        string spake2Pub = "", ltPub = "", ip = "", deviceType = "unknown";
                        try
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(data);
                            spake2Pub = doc.RootElement.GetProperty("spake2_pub").GetString() ?? "";
                            ltPub = doc.RootElement.GetProperty("lt_pub").GetString() ?? "";
                            ip = doc.RootElement.GetProperty("ip").GetString() ?? "";
                            deviceType = doc.RootElement.GetProperty("device_type").GetString() ?? "unknown";
                        }
                        catch { }
                        var ns = NetworkService;
                        if (ns == null) return;
                        _ = ns.HandlePairingRespAsync(uuid, spake2Pub, ltPub, ip, intValue, deviceType);
                    }
                    break;
                case "ACCEPT":
                    {
                        if (data == null) return;
                        string ltPubKey = "", ip = "", deviceType = "unknown";
                        try
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(data);
                            ltPubKey = doc.RootElement.GetProperty("lt_pub_key").GetString() ?? "";
                            ip = doc.RootElement.GetProperty("ip").GetString() ?? "";
                            deviceType = doc.RootElement.GetProperty("device_type").GetString() ?? "unknown";
                        }
                        catch { }
                        var ns = NetworkService;
                        if (ns == null) return;
                        _ = ns.HandlePairingAcceptAsync(uuid, ltPubKey, ip, intValue, deviceType);
                    }
                    break;
                case "REJECT":
                    {
                        var ns = NetworkService;
                        if (ns == null) return;
                        _ = ns.HandleRejectAsync(uuid);
                    }
                    break;
                case "RESULT":
                    {
                        var ns = NetworkService;
                        if (ns == null) return;
                        _ = ns.HandlePairingResultAsync(uuid, intValue, extra ?? "");
                    }
                    break;
                case "HEARTBEAT_TCP":
                    {
                        var device = DeviceManager?.FindDeviceById(uuid);
                        if (device != null)
                        {
                            device.LastHeartbeat = DateTime.UtcNow;
                            if (!string.IsNullOrEmpty(extra))
                            {
                                // 回调运行在 Rust 线程，绑定到 Name 的 UI 元素需在 UI 线程更新
                                App.MainWindow?.DispatcherQueue?.TryEnqueue(() =>
                                {
                                    device.Name = extra;
                                });
                                DeviceManager?.SaveDevice(device);
                            }
                        }
                    }
                    break;
            }
        };
        NotifyRelayCore.nrc_set_on_pairing_cb(_ctx, onPairingCb);
        _callbackRefs.Add(onPairingCb);

        NotifyRelayCore.OnDataCb onDataCb = (uuidPtr, msgTypePtr, plaintextPtr, userData) =>
        {
            var device = FindDevice(uuidPtr);
            var msgType = Marshal.PtrToStringUTF8(msgTypePtr);
            var text = Marshal.PtrToStringUTF8(plaintextPtr);
            System.Diagnostics.Debug.WriteLine($"[CoreCb] 收到数据: 类型={msgType}, 设备={device?.Id}, 长度={text?.Length}, 设备匹配={device != null}");
            if (device == null || text == null || msgType == null) return;

            switch (msgType)
            {
                case "NOTIFICATION":
                    _ = ProtocolRouter?.OnDataNotificationAsync(device, text) ?? Task.CompletedTask;
                    break;
                case "MEDIAPLAY":
                    _ = ProtocolRouter?.OnDataMediaPlayAsync(device, text) ?? Task.CompletedTask;
                    break;
                case "ICON_REQUEST":
                    _ = ProtocolRouter?.OnDataIconRequestAsync(device, text) ?? Task.CompletedTask;
                    break;
                case "ICON_RESPONSE":
                    _ = ProtocolRouter?.OnDataIconResponseAsync(device, text) ?? Task.CompletedTask;
                    break;
                case "APP_LIST_REQUEST":
                    _ = ProtocolRouter?.OnDataAppListRequestAsync(device, text) ?? Task.CompletedTask;
                    break;
                case "APP_LIST_RESPONSE":
                    _ = ProtocolRouter?.OnDataAppListResponseAsync(device, text) ?? Task.CompletedTask;
                    break;
                case "MEDIA_CONTROL":
                    _ = ProtocolRouter?.OnDataMediaControlAsync(device, text) ?? Task.CompletedTask;
                    break;
                case "FTP":
                    _ = ProtocolRouter?.OnDataFtpAsync(device, text) ?? Task.CompletedTask;
                    break;
                case "CLIPBOARD":
                    _ = ProtocolRouter?.OnDataClipboardAsync(device, text) ?? Task.CompletedTask;
                    break;
                case "STATUS":
                    _ = ProtocolRouter?.OnDataStatusAsync(device, text) ?? Task.CompletedTask;
                    break;
                case "APP_LAUNCH":
                    _ = ProtocolRouter?.OnDataAppListRequestAsync(device, text) ?? Task.CompletedTask;
                    break;
                case "SUPERISLAND":
                    _ = ProtocolRouter?.OnDataSuperIslandAsync(device, text) ?? Task.CompletedTask;
                    break;
            }
        };
        NotifyRelayCore.nrc_set_on_data_cb(_ctx, onDataCb);
        _callbackRefs.Add(onDataCb);

        // ---- on_state_query (超级岛/媒体心跳查询回调：0=不存在 / 1=存在无变更 / 2=存在有变更) ----
        // 运行在 Rust 心跳线程且锁已释放；PC 仅作为媒体发送端：
        // 媒体会话存在性由 WindowsPlaybackService 提供（MediaSessionQueryHandler），
        // 无活跃会话 → 0（Rust 移除会话）；有 → 1 保活（状态变更由事件驱动推送）。
        NotifyRelayCore.OnStateQueryCb onStateQueryCb = (uuidPtr, featureIdPtr, isMedia, userData) =>
        {
            var uuid = Marshal.PtrToStringUTF8(uuidPtr);
            var featureId = Marshal.PtrToStringUTF8(featureIdPtr);
            if (uuid == null || featureId == null) return 0;
            if (isMedia == 0) return 0; // PC 仅发送媒体会话，无超级岛会话
            try
            {
                return MediaSessionQueryHandler?.Invoke(uuid) == true ? 1 : 0;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CoreCb] on_state_query error: {ex.Message}");
                return 1; // 异常保守保活，等待下一次查询
            }
        };
        NotifyRelayCore.nrc_set_on_state_query_cb(_ctx, onStateQueryCb);
        _callbackRefs.Add(onStateQueryCb);

        NotifyRelayCore.OnMdnsDiscoveredCb onMdnsDiscoveredCb = (uuidPtr, namePtr, ipPtr, port, battery, deviceTypePtr, userData) =>
        {
            var uuid = Marshal.PtrToStringUTF8(uuidPtr);
            var name = Marshal.PtrToStringUTF8(namePtr);
            var ip = Marshal.PtrToStringUTF8(ipPtr);
            var deviceType = Marshal.PtrToStringUTF8(deviceTypePtr) ?? "unknown";
            if (uuid == null || ip == null) return;
            System.Diagnostics.Debug.WriteLine($"[CoreCb] 发现设备: {uuid}, ip={ip}, 名称={name}, 端口={port}, 电量={battery}");

            var hp = HeartbeatProcessor;
            if (hp == null) return;
            hp.HandleMdnsDiscovered(uuid, name, ip, port, battery, deviceType);
        };
        NotifyRelayCore.nrc_set_on_mdns_discovered_cb(_ctx, onMdnsDiscoveredCb);
        _callbackRefs.Add(onMdnsDiscoveredCb);

        NotifyRelayCore.OnDeviceTimeoutCb onDeviceTimeoutCb = (uuidPtr, userData) =>
        {
            var uuid = Marshal.PtrToStringUTF8(uuidPtr);
            if (uuid == null) return;
            // 设备真离线（超时未响应），打印并清除在线状态，下次重新上线时"设备已连接"会再次打印
            if (_deviceOnline.TryRemove(uuid, out _))
            {
                System.Diagnostics.Debug.WriteLine($"[CoreCb] 设备已离线: {uuid}");
            }
            var dispatcher = App.MainWindow?.DispatcherQueue;
            if (dispatcher != null && !dispatcher.HasThreadAccess)
            {
                dispatcher.TryEnqueue(() => HandleDeviceTimeout(uuid));
            }
            else
            {
                HandleDeviceTimeout(uuid);
            }
        };
        NotifyRelayCore.nrc_set_on_device_timeout_cb(_ctx, onDeviceTimeoutCb);
        _callbackRefs.Add(onDeviceTimeoutCb);

        NotifyRelayCore.OnDeviceConnectedCb onDeviceConnectedCb = (uuidPtr, ipPtr, userData) =>
        {
            var uuid = Marshal.PtrToStringUTF8(uuidPtr);
            var ip = Marshal.PtrToStringUTF8(ipPtr) ?? "";
            if (uuid == null) return;
            // 仅首次上线（或离线后重新上线）打印；维持性心跳连接的反复上下线静默
            if (_deviceOnline.TryAdd(uuid, 0))
            {
                System.Diagnostics.Debug.WriteLine($"[CoreCb] 设备已连接: {uuid} ({ip})");
            }
        };
        NotifyRelayCore.nrc_set_on_device_connected_cb(_ctx, onDeviceConnectedCb);
        _callbackRefs.Add(onDeviceConnectedCb);

        NotifyRelayCore.OnDeviceDisconnectedCb onDeviceDisconnectedCb = (uuidPtr, userData) =>
        {
            // 心跳维持连接的断开不打印，真实离线由 on_device_timeout（超时检测）体现
        };
        NotifyRelayCore.nrc_set_on_device_disconnected_cb(_ctx, onDeviceDisconnectedCb);
        _callbackRefs.Add(onDeviceDisconnectedCb);

        NotifyRelayCore.OnTcpErrorCb onTcpErrorCb = (errorPtr, userData) =>
        {
            var error = Marshal.PtrToStringUTF8(errorPtr) ?? "unknown";
            System.Diagnostics.Debug.WriteLine($"[CoreCb] TCP 错误: {error}");
        };
        NotifyRelayCore.nrc_set_on_tcp_error_cb(_ctx, onTcpErrorCb);
        _callbackRefs.Add(onTcpErrorCb);
    }

    // ======== Heartbeat scheduler ========
    private static long _senderQueueHandle;

    public static void UpdateHeartbeatSchedulerParams(string name, int battery, string deviceType)
    {
        NotifyRelayCore.Safe.UpdateHeartbeatSchedulerParams(_ctx, name, battery, deviceType);
    }

    // ======== Device state snapshot ========
    public static string? GetDeviceList(long authedTimeoutMs = 12000, long unauthedTimeoutMs = 5000)
    {
        return NotifyRelayCore.Safe.GetDeviceList(_ctx, authedTimeoutMs, unauthedTimeoutMs);
    }

    // ======== Sender queue ========
    public static long SenderQueueHandle => _senderQueueHandle;

    public static void EnqueueMessage(string deviceUuid, string header, string plaintext, string? dedupKey = null)
    {
        NotifyRelayCore.Safe.EnqueueMessage(_ctx, _senderQueueHandle, deviceUuid, header, plaintext, dedupKey);
    }

    // 推送「全量」超级岛/媒体状态；Rust 内部 diff 并经 on_data 回调回传合并后的全量。
    public static void PushSuperIslandState(string deviceUuid, string fullJson, bool isEnd = false)
    {
        NotifyRelayCore.Safe.PushSuperIslandState(_ctx, _senderQueueHandle, deviceUuid, fullJson, isEnd);
    }

    public static void PushMediaState(string deviceUuid, string fullJson, bool isEnd = false)
    {
        NotifyRelayCore.Safe.PushMediaState(_ctx, _senderQueueHandle, deviceUuid, fullJson, isEnd);
    }

    // ======== Clipboard ========
    public static string? ClipboardOnChanged(string targetsJson, string mime, string content, bool force)
    {
        return NotifyRelayCore.Safe.ClipboardOnChanged(_ctx, _senderQueueHandle, targetsJson, mime, content, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), force);
    }

    public static string? ClipboardOnReceived(string payloadJson)
    {
        return NotifyRelayCore.Safe.ClipboardOnReceived(_ctx, payloadJson, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    // ======== App sync (app list & icons) ========
    public static string? AppSyncPrepareIconRequest(string packagesJson, string installedJson, string cachedJson, string appDeviceJson, string sourceDeviceUuid)
    {
        return NotifyRelayCore.Safe.AppSyncPrepareIconRequest(_ctx, packagesJson, installedJson, cachedJson, appDeviceJson, sourceDeviceUuid, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public static void AppSyncClearIconPending(string packagesJson)
    {
        NotifyRelayCore.Safe.AppSyncClearIconPending(_ctx, packagesJson);
    }

    public static string? AppSyncParseIconResponse(string payloadJson)
    {
        return NotifyRelayCore.Safe.AppSyncParseIconResponse(payloadJson);
    }

    public static string? AppSyncBuildApplistRequest(string scope = "user")
    {
        return NotifyRelayCore.Safe.AppSyncBuildApplistRequest(scope, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public static string? AppSyncParseApplistResponse(string payloadJson)
    {
        return NotifyRelayCore.Safe.AppSyncParseApplistResponse(payloadJson);
    }

    // ======== Network change ========
    public static void OnNetworkChanged(string? localIp = null)
    {
        if (localIp is not null)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(localIp);
            var ipPtr = Marshal.AllocHGlobal(bytes.Length + 1);
            Marshal.Copy(bytes, 0, ipPtr, bytes.Length);
            Marshal.WriteByte(ipPtr, bytes.Length, 0);
            NotifyRelayCore.nrc_on_network_changed(_ctx, ipPtr);
            Marshal.FreeHGlobal(ipPtr);
        }
        else
        {
            NotifyRelayCore.nrc_on_network_changed(_ctx, IntPtr.Zero);
        }
    }

    // ======== Local IP ========
    public static string? GetLocalIp()
    {
        return NotifyRelayCore.PtrToStringAndFree(NotifyRelayCore.nrc_get_local_ip());
    }

    // ======== Discovery ========
    public static void AddKnownDevice(string uuid, string ip)
    {
        NotifyRelayCore.Safe.AddKnownDevice(_ctx, uuid, ip);
    }

    public static void RemoveKnownDevice(string uuid)
    {
        NotifyRelayCore.Safe.RemoveKnownDevice(_ctx, uuid);
    }

    // ======== mDNS ========
    public static void StopMdnsAdvertiser()
    {
        NotifyRelayCore.Safe.StopMdnsAdvertiser(_ctx);
    }

    public static void StopMdnsDiscovery()
    {
        NotifyRelayCore.Safe.StopMdnsDiscovery(_ctx);
    }

    public static int AudioStart(string direction, int sampleRate, int channels, string remoteUuid)
    {
        return NotifyRelayCore.Safe.AudioStart(_ctx, direction, 23335, sampleRate, channels, remoteUuid);
    }

    public static int AudioWriteFrame(byte[] pcm)
    {
        return NotifyRelayCore.Safe.AudioWriteFrame(_ctx, pcm, pcm.Length);
    }

    public static int AudioStop()
    {
        return NotifyRelayCore.Safe.AudioStop(_ctx);
    }

    public static int AudioIsActive()
    {
        return NotifyRelayCore.Safe.AudioIsActive(_ctx);
    }

    public static void RegisterAudioCallbacks(NotifyRelayCore.AudioDataCallback dataCb, NotifyRelayCore.AudioEventCallback eventCb)
    {
        NotifyRelayCore.Safe.RegisterAudioDataCb(_ctx, dataCb);
        NotifyRelayCore.Safe.RegisterAudioEventCb(_ctx, eventCb);
        _callbackRefs.Add(dataCb);
        _callbackRefs.Add(eventCb);
    }

    private static void HandleDeviceTimeout(string uuid)
    {
        var device = DeviceManager?.FindDeviceById(uuid);
        if (device == null) return;
        device.ConnectionStatus = false;
        device.Session = null;
        NetworkService?.DisconnectDevice(uuid);
    }
}
