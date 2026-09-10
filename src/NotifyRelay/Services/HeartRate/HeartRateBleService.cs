using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using NotifyRelay.Data.Contracts;

namespace NotifyRelay.Services.HeartRate;

/// <summary>心率 BLE 连接状态。</summary>
public enum HeartRateConnectionState
{
    Disconnected,
    Scanning,
    Connecting,
    Connected,
    /// <summary>意外断线后后台自动重连中。</summary>
    Reconnecting
}

/// <summary>扫描发现的心率设备信息。</summary>
public sealed class HeartRateDeviceInfo
{
    public ulong Address { get; init; }
    public string Name { get; init; } = string.Empty;
    public override string ToString() => Name;
}

/// <summary>
/// BLE 心率服务：扫描（仅广播标准心率服务 0x180D 的设备）、手动连接、
/// 订阅心率测量特征值 0x2A37 并按标准格式解析，断开与状态事件。
/// 会话内记住最后一次手动连接成功的设备地址，意外断线时后台自动重连
/// （用户主动断开则本次会话不重连）；开启"启动时自动连接"后，应用启动时
/// 自动连接上次手动连接的设备（地址跨重启持久化）。
/// </summary>
public sealed class HeartRateBleService : IDisposable
{
    private static readonly Guid HeartRateServiceUuid = GattServiceUuids.HeartRate;                 // 0000180D-...
    private static readonly Guid HeartRateMeasurementUuid = GattCharacteristicUuids.HeartRateMeasurement; // 00002A37-...

    /// <summary>意外断线后自动重连的首次重试间隔。</summary>
    private static readonly TimeSpan ReconnectInitialInterval = TimeSpan.FromSeconds(1);

    /// <summary>自动重连指数退避的间隔上限（长时间连不上时降为每分钟一次）。</summary>
    private static readonly TimeSpan ReconnectMaxInterval = TimeSpan.FromSeconds(60);

    private readonly ILogger<HeartRateBleService> _logger;
    private readonly IGeneralSettingsService _settings;
    private readonly object _sync = new();
    // 连接过程串行化，防止手动连接与后台重连并发建立双连接
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    private BluetoothLEAdvertisementWatcher? _watcher;
    private BluetoothLEDevice? _device;
    private GattDeviceService? _service;
    private GattCharacteristic? _characteristic;

    // 会话内记忆的自动重连目标地址（仅手动连接成功时写入；用户主动断开时清空）
    private ulong? _autoReconnectAddress;
    private CancellationTokenSource? _reconnectCts;

    private volatile HeartRateConnectionState _state = HeartRateConnectionState.Disconnected;

    /// <summary>收到心率值（BLE 回调线程触发）。</summary>
    public event Action<int>? HeartRateReceived;

    /// <summary>连接状态变化（任意线程触发）。</summary>
    public event Action<HeartRateConnectionState>? StateChanged;

    /// <summary>扫描期间发现新设备（任意线程触发）。</summary>
    public event Action<HeartRateDeviceInfo>? DeviceDiscovered;

    public HeartRateConnectionState State => _state;

    public HeartRateBleService(ILogger<HeartRateBleService> logger, IGeneralSettingsService settings)
    {
        _logger = logger;
        _settings = settings;
    }

    private void SetState(HeartRateConnectionState state)
    {
        if (_state == state) return;
        _state = state;
        _logger.LogInformation("心率 BLE 状态变更: {State}", state);
        StateChanged?.Invoke(state);
    }

    /// <summary>开始扫描广播心率服务的 BLE 设备；重复调用会先停止上一次扫描，并取消进行中的自动重连。</summary>
    public void StartScan()
    {
        CancelReconnect();
        lock (_sync)
        {
            StopScanInternal();

            var watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active
            };
            // 仅列出广播标准心率服务 0x180D 的设备
            watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(HeartRateServiceUuid);

            var seen = new HashSet<ulong>();
            watcher.Received += (_, args) =>
            {
                string name = args.Advertisement.LocalName;
                if (string.IsNullOrWhiteSpace(name)) name = $"未知设备 ({args.BluetoothAddress:X12})";
                lock (seen)
                {
                    if (!seen.Add(args.BluetoothAddress)) return;
                }
                DeviceDiscovered?.Invoke(new HeartRateDeviceInfo { Address = args.BluetoothAddress, Name = name });
            };

            _watcher = watcher;
            try
            {
                watcher.Start();
                SetState(HeartRateConnectionState.Scanning);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "心率 BLE 扫描启动失败（蓝牙可能未开启）");
                _watcher = null;
                try { watcher.Stop(); } catch { }
                SetState(_characteristic != null
                    ? HeartRateConnectionState.Connected
                    : HeartRateConnectionState.Disconnected);
            }
        }
    }

    /// <summary>停止扫描（不影响已建立的连接）。</summary>
    public void StopScan()
    {
        lock (_sync)
        {
            StopScanInternal();
            if (_state == HeartRateConnectionState.Scanning)
                SetState(_characteristic != null ? HeartRateConnectionState.Connected : HeartRateConnectionState.Disconnected);
        }
    }

    private void StopScanInternal()
    {
        if (_watcher != null)
        {
            try { _watcher.Stop(); } catch { /* 忽略停止异常 */ }
            _watcher = null;
        }
    }

    /// <summary>手动连接指定地址的设备并订阅心率通知；成功后会话内记忆该地址用于断线自动重连。</summary>
    public async Task<bool> ConnectAsync(ulong address)
    {
        CancelReconnect();
        CleanupConnection();
        lock (_sync) StopScanInternal();
        SetState(HeartRateConnectionState.Connecting);

        bool ok = await ConnectCoreAsync(address);
        if (ok)
        {
            lock (_sync) _autoReconnectAddress = address;
            // 持久化最后连接地址，供"启动时自动连接"使用（用户主动断开不清除）
            try { _settings.HeartRateLastDeviceAddress = address.ToString(); }
            catch (Exception ex) { _logger.LogWarning(ex, "保存心率设备地址失败"); }
        }
        else
        {
            SetState(HeartRateConnectionState.Disconnected);
        }
        return ok;
    }

    /// <summary>实际连接与订阅逻辑（经 _connectLock 串行化）；失败仅清理资源，不改状态、不触发重连。</summary>
    private async Task<bool> ConnectCoreAsync(ulong address, CancellationToken ct = default)
    {
        await _connectLock.WaitAsync(ct);
        try
        {
            var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            if (device == null)
            {
                _logger.LogWarning("心率 BLE 连接失败：无法获取设备对象 {Address:X12}", address);
                return false;
            }

            var servicesResult = await device.GetGattServicesForUuidAsync(HeartRateServiceUuid, BluetoothCacheMode.Uncached);
            if (servicesResult.Status != GattCommunicationStatus.Success || servicesResult.Services.Count == 0)
            {
                _logger.LogWarning("心率 BLE 连接失败：未找到心率服务，状态 {Status}", servicesResult.Status);
                device.Dispose();
                return false;
            }
            var service = servicesResult.Services[0];

            var charResult = await service.GetCharacteristicsForUuidAsync(HeartRateMeasurementUuid, BluetoothCacheMode.Uncached);
            if (charResult.Status != GattCommunicationStatus.Success || charResult.Characteristics.Count == 0)
            {
                _logger.LogWarning("心率 BLE 连接失败：未找到心率测量特征值，状态 {Status}", charResult.Status);
                service.Dispose();
                device.Dispose();
                return false;
            }
            var characteristic = charResult.Characteristics[0];

            var status = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);
            if (status != GattCommunicationStatus.Success)
            {
                _logger.LogWarning("心率 BLE 连接失败：订阅通知失败，状态 {Status}", status);
                service.Dispose();
                device.Dispose();
                return false;
            }

            characteristic.ValueChanged += OnHeartRateValueChanged;
            device.ConnectionStatusChanged += OnConnectionStatusChanged;

            lock (_sync)
            {
                _device = device;
                _service = service;
                _characteristic = characteristic;
            }
            SetState(HeartRateConnectionState.Connected);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw; // 取消由重连循环处理
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "心率 BLE 连接异常 {Address:X12}", address);
            CleanupConnection();
            return false;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private void OnHeartRateValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            var reader = DataReader.FromBuffer(args.CharacteristicValue);
            byte[] data = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(data);
            if (data.Length < 2) return;

            // 标准心率测量格式：flags bit0 = 1 时为 uint16 (little-endian)，否则为 uint8
            int bpm = (data[0] & 0x01) != 0 && data.Length >= 3
                ? data[1] | (data[2] << 8)
                : data[1];
            if (bpm > 0)
                HeartRateReceived?.Invoke(bpm);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "心率数据解析失败");
        }
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus != BluetoothConnectionStatus.Disconnected) return;

        _logger.LogInformation("心率 BLE 设备已断开连接");
        CleanupConnection();

        // 意外断线：若存在会话内记忆地址则启动自动重连，否则回到未连接
        ulong? address;
        lock (_sync) address = _autoReconnectAddress;
        if (address.HasValue)
        {
            StartReconnect(address.Value);
        }
        else
        {
            SetState(HeartRateConnectionState.Disconnected);
        }
    }

    /// <summary>启动后台自动重连循环（先取消上一次循环）。</summary>
    private void StartReconnect(ulong address)
    {
        CancellationTokenSource cts;
        lock (_sync)
        {
            _reconnectCts?.Cancel();
            _reconnectCts?.Dispose();
            cts = new CancellationTokenSource();
            _reconnectCts = cts;
        }
        SetState(HeartRateConnectionState.Reconnecting);
        _ = Task.Run(() => ReconnectLoopAsync(address, cts.Token), CancellationToken.None);
    }

    /// <summary>后台重连循环：指数退避重试（首次 1s、每次翻倍、上限 60s），直到连上或被取消。</summary>
    private async Task ReconnectLoopAsync(ulong address, CancellationToken ct)
    {
        try
        {
            var delay = ReconnectInitialInterval;
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(delay, ct);
                _logger.LogInformation("心率 BLE 自动重连尝试 {Address:X12}", address);
                bool ok = await ConnectCoreAsync(address, ct);
                if (ok) return; // ConnectCoreAsync 成功时已置 Connected
                if (ct.IsCancellationRequested) return;
                // 失败保持 Reconnecting 状态，等待下一轮；间隔翻倍并封顶
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, ReconnectMaxInterval.Ticks));
            }
        }
        catch (OperationCanceledException)
        {
            // 用户取消或对象释放，正常退出
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "心率 BLE 自动重连循环异常，停止重连");
            if (!ct.IsCancellationRequested)
                SetState(HeartRateConnectionState.Disconnected);
        }
    }

    /// <summary>取消进行中的自动重连循环（不清理已建立连接、不清记忆地址）。</summary>
    private void CancelReconnect()
    {
        lock (_sync)
        {
            if (_reconnectCts == null) return;
            try { _reconnectCts.Cancel(); } catch { /* 忽略取消异常 */ }
            _reconnectCts.Dispose();
            _reconnectCts = null;
        }
    }

    /// <summary>仅释放当前连接资源并注销回调，不改动状态机、不影响重连意图。</summary>
    private void CleanupConnection()
    {
        BluetoothLEDevice? device;
        GattDeviceService? service;
        GattCharacteristic? characteristic;
        lock (_sync)
        {
            device = _device;
            service = _service;
            characteristic = _characteristic;
            _device = null;
            _service = null;
            _characteristic = null;
        }

        if (characteristic != null)
        {
            characteristic.ValueChanged -= OnHeartRateValueChanged;
            try
            {
                _ = characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None);
            }
            catch { /* 忽略取消订阅异常 */ }
        }
        if (device != null)
        {
            device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        }
        service?.Dispose();
        device?.Dispose();
    }

    /// <summary>用户主动断开：取消自动重连、清除记忆地址、释放连接并置为未连接。</summary>
    public void Disconnect()
    {
        CancelReconnect();
        lock (_sync) _autoReconnectAddress = null;
        CleanupConnection();
        if (_state != HeartRateConnectionState.Scanning)
            SetState(HeartRateConnectionState.Disconnected);
    }

    /// <summary>
    /// 应用启动时尝试自动连接上次连接的设备：需开启"启动时自动连接"开关
    /// 且存在持久化地址。复用断线自动重连循环，后台按固定间隔重试直到连上
    /// 或被用户操作（扫描/手动连接/断开）取消。
    /// </summary>
    public void TryAutoConnectOnStartup()
    {
        try
        {
            if (!_settings.HeartRateAutoConnectEnabled) return;
            string saved = _settings.HeartRateLastDeviceAddress;
            if (string.IsNullOrWhiteSpace(saved) || !ulong.TryParse(saved, out ulong address) || address == 0)
                return;

            lock (_sync)
            {
                if (_state != HeartRateConnectionState.Disconnected || _device != null) return;
                // 写入会话内记忆地址：连上后若意外断线可继续走既有断链复联逻辑
                _autoReconnectAddress = address;
            }
            _logger.LogInformation("心率 BLE 启动自动连接 {Address:X12}", address);
            StartReconnect(address);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "心率 BLE 启动自动连接失败");
        }
    }

    public void Dispose()
    {
        lock (_sync) StopScanInternal();
        Disconnect();
        _connectLock.Dispose();
    }
}
