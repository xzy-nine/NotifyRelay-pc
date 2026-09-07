using NotifyRelay.Data.AppDatabase;
using NotifyRelay.Data.AppDatabase.Models;
using NotifyRelay.Data.AppDatabase.Repository;
using NotifyRelay.Data.Contracts;
using NotifyRelay.Native;
using NotifyRelay.Platforms.Windows;
using NotifyRelay.Platforms.Windows.Services;
using NotifyRelay.Services;
using NotifyRelay.Services.Filters;
using NotifyRelay.Services.Overlay;
using NotifyRelay.Services.OverlayFeatures;
using NotifyRelay.Services.Settings;
using NotifyRelay.ViewModels;
using NotifyRelay.ViewModels.Settings;
using Serilog;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace NotifyRelay.Helpers;

/// <summary>
/// Provides static helper to manage app lifecycle.
/// </summary>
public static class AppLifecycleHelper
{
    /// <summary>
    /// Gets application package version.
    /// </summary>
    public static Version AppVersion { get; } =
        new(Package.Current.Id.Version.Major, Package.Current.Id.Version.Minor, Package.Current.Id.Version.Build, Package.Current.Id.Version.Revision);

    public static async Task InitializeAppComponentsAsync()
    {
        var logger = Ioc.Default.GetRequiredService<ILogger<App>>();
        logger.LogInformation("开始初始化应用组件...");

        logger.LogInformation("步骤1-3：初始化数据库并预热...");
        var databaseContext = Ioc.Default.GetRequiredService<DatabaseContext>();
        var deviceRepository = Ioc.Default.GetRequiredService<DeviceRepository>();
        var localDevice = deviceRepository.GetLocalDevice();
        if (localDevice is null)
            logger.LogInformation("数据库预热：本地设备不存在，将在后续生成");
        else
            logger.LogInformation("数据库预热：找到本地设备，DeviceId: {deviceId}", localDevice.DeviceId);
        logger.LogInformation("数据库预热完成");

        logger.LogInformation("步骤4-11：获取服务...");
        var networkService = Ioc.Default.GetRequiredService<INetworkService>();
        var discoveryService = Ioc.Default.GetRequiredService<IDiscoveryService>();
        var notificationService = Ioc.Default.GetRequiredService<INotificationService>();
        var deviceManager = Ioc.Default.GetRequiredService<IDeviceManager>();
        var adbService = Ioc.Default.GetRequiredService<IAdbService>();
        var playbackService = Ioc.Default.GetRequiredService<IPlaybackService>();
        var actionService = Ioc.Default.GetRequiredService<IActionService>();
        var updateService = Ioc.Default.GetRequiredService<IUpdateService>();
        logger.LogInformation("服务获取成功");

        // ===== 并行阶段A：无依赖服务同时启动 =====
        logger.LogInformation("步骤12-13-16-18-19：并行启动无依赖服务...");
        await Task.WhenAll(
            RegisterWindowsNotificationAsync(logger),
            InitRustCoreAsync(logger),
            StartLocalSocketRelayAsync(logger),
            InitWorkerConfigAsync(logger),
            InitAudioRelayAsync(logger)
        );
        logger.LogInformation("无依赖服务并行启动完成");

        // ===== 串行阶段B：依赖 Rust Core 的关键链 =====
        logger.LogInformation("步骤14：生成并初始化UUID...");
        localDevice = await deviceManager.GetLocalDeviceAsync();
        logger.LogInformation("步骤14：UUID初始化完成，DeviceId: {deviceId}", localDevice.DeviceId);
        // 注意：此处不读取 Rust UUID——升级用户以平台表 DeviceId 为迁移种子，
        // 由 StartCore（步骤17）传入 Rust 库；全新安装用户在 GetLocalDeviceAsync 首启已生成
        // 一致性对齐在步骤17b（FinalizeRustPersistenceAsync）进行

        logger.LogInformation("步骤15：初始化设备管理器...");
        await deviceManager.Initialize();
        logger.LogInformation("步骤15：设备管理器初始化完成");

        // 清理历史残留的本机配对记录（自我握手循环等异常写入），避免登记到 known_devices 引发自我连接
        try
        {
            var staleSelf = deviceManager.PairedDevices.FirstOrDefault(d => d.Id == localDevice.DeviceId);
            if (staleSelf is not null)
            {
                logger.LogWarning("检测到本机残留配对记录，自动清理: {deviceId}", staleSelf.Id);
                deviceManager.RemoveDevice(staleSelf);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "清理本机残留配对记录失败");
        }

        // 密钥迁移（轻量同步操作，必须在服务启动前完成）
        logger.LogInformation("步骤15a：迁移已有设备密钥到 Rust...");
        bool allMigrationsSucceeded = true;
        try
        {
            var deviceRepo = Ioc.Default.GetRequiredService<DeviceRepository>();
            int migratedCount = 0;
            foreach (var device in deviceRepo.GetRemoteDevices())
            {
                if (device.DeviceId == localDevice.DeviceId) continue;
                if (device.SharedSecret is { Length: 32 })
                {
                    if (NativeCore.MigrateSharedSecret(device.DeviceId, device.SharedSecret) != 0)
                    {
                        logger.LogWarning("步骤15a：设备 {deviceId} 密钥迁移持久化失败，保留旧密钥", device.DeviceId);
                        allMigrationsSucceeded = false;
                        continue;
                    }
                    NativeCore.RenameDevice(device.DeviceId, string.IsNullOrEmpty(device.Name) ? device.DeviceId : device.Name);
                    migratedCount++;
                }
            }
            logger.LogInformation("步骤15a：已迁移 {count} 个设备密钥", migratedCount);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "步骤15a：迁移旧设备密钥失败");
            allMigrationsSucceeded = false;
        }

        logger.LogInformation("步骤15：初始化通知服务...");
        notificationService.Initialize();
        logger.LogInformation("步骤15：通知服务初始化完成");

        // 过滤配置
        logger.LogInformation("步骤15a：初始化通知过滤配置...");
        try
        {
            var filterConfigRepository = Ioc.Default.GetRequiredService<FilterConfigRepository>();
            var filterConfig = filterConfigRepository.LoadOrCreateDefault();
            var remoteFilter = Ioc.Default.GetRequiredService<BackendRemoteFilter>();
            filterConfig.ApplyTo(remoteFilter);
            filterConfig.ApplyLocalFilter();
            logger.LogInformation("步骤15a：通知过滤配置初始化完成");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "步骤15a：初始化通知过滤配置失败");
        }

        // 本地通知监听后台启动
        _ = StartLocalNotificationListenerAsync(logger);

        // 启动叠加层渲染引擎
        try
        {
            var overlay = Ioc.Default.GetRequiredService<OverlayRenderService>();
            overlay.Start();
            logger.LogInformation("Overlay渲染引擎启动成功");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "启动Overlay渲染引擎失败");
        }


        // ===== 并行阶段C：核心服务启动 =====
        logger.LogInformation("步骤17：启动核心服务...");
        var tcpServerTask = networkService.StartServerAsync();
        var discoveryTask = discoveryService.StartDiscoveryAsync();
        var playbackTask = playbackService.InitializeAsync();
        logger.LogInformation("步骤17：3个关键子任务已创建");

        // 监控各子任务完成状态，便于定位启动卡点
        _ = tcpServerTask.ContinueWith(t => LogSubtaskDone(logger, "TCP服务器", t), TaskScheduler.Default);
        _ = discoveryTask.ContinueWith(t => LogSubtaskDone(logger, "Discovery", t), TaskScheduler.Default);
        _ = playbackTask.ContinueWith(t => LogSubtaskDone(logger, "Playback", t), TaskScheduler.Default);

        await Task.WhenAll(tcpServerTask, discoveryTask, playbackTask);
        logger.LogInformation("步骤17：核心服务启动完成");

        // ADB服务在后台启动，不阻塞主流程（DeviceMonitor.StartAsync 是长生命周期监控任务，不会自然结束）
        _ = adbService.StartAsync().ContinueWith(t => LogSubtaskDone(logger, "ADB", t), TaskScheduler.Default);

        // 步骤17b：Rust 持久化收尾（uuid 已进入核心，触发落盘后清理平台旧存储）
        try
        {
            await FinalizeRustPersistenceAsync(logger, localDevice, allMigrationsSucceeded);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "步骤17b：Rust 持久化收尾失败");
        }

        // 非关键服务后台启动（不阻塞主流程）
        _ = actionService.InitializeAsync();
        _ = updateService.CheckForUpdatesAsync();

        logger.LogInformation("步骤21：初始化完成，关闭启动画面");
        App.SplashScreenLoadingTCS?.TrySetResult();
        logger.LogInformation("应用组件初始化全部完成");
    }

    /// <summary>
    /// 步骤17b：Rust 持久化收尾（start_core 已传入本机 uuid）
    /// - GetLocalUuid 触发自动落盘并校验
    /// - 平台表 DeviceId 与库对齐
    /// - 清理旧平台存储：LocalDeviceEntity.StateJson 值、RemoteDeviceEntity.SharedSecret 列值
    /// </summary>
    private static async Task FinalizeRustPersistenceAsync(ILogger logger, LocalDeviceEntity localDevice, bool allMigrationsSucceeded)
    {
        var rustUuid = NativeCore.GetLocalUuid();
        if (string.IsNullOrEmpty(rustUuid))
        {
            logger.LogWarning("步骤17b：Rust 持久化未就绪，暂缓清理旧平台存储");
            return;
        }
        var repo = Ioc.Default.GetRequiredService<DeviceRepository>();

        if (rustUuid != localDevice.DeviceId)
        {
            logger.LogInformation("步骤17b：UUID 以 Rust 持久化为准: {rustUuid} (原: {oldId})", rustUuid, localDevice.DeviceId);
            var oldId = localDevice.DeviceId;
            localDevice.DeviceId = rustUuid;
            if (!repo.RenameLocalDeviceKey(oldId, rustUuid))
            {
                logger.LogWarning("步骤17b：平台主键更新失败，保持旧 DeviceId 以维护内存与数据库一致性");
                localDevice.DeviceId = oldId;
                return;
            }
        }

        if (!string.IsNullOrEmpty(localDevice.StateJson))
        {
            localDevice.StateJson = string.Empty;
            repo.AddOrUpdateLocalDevice(localDevice);
            logger.LogInformation("步骤17b：已清理旧加密状态 blob（密钥由 Rust 私有库持有）");
        }

        // 远程设备旧密钥列值已全部迁移至 Rust，清空平台存储
        // 仅当所有设备迁移均成功且持久化已确认后才清理，否则保留旧密钥允许下次启动重试
        if (allMigrationsSucceeded)
        {
            int cleared = repo.ClearRemoteSecrets();
            if (cleared > 0)
            {
                logger.LogInformation("步骤17b：已清空 {count} 条旧设备密钥记录", cleared);
            }
        }
        else
        {
            logger.LogWarning("步骤17b：存在迁移失败的设备，跳过清空旧密钥列，下次启动将重试");
        }

        await Task.CompletedTask;
    }

    private static void LogSubtaskDone(ILogger logger, string name, Task task)
    {
        if (task.IsFaulted)
            logger.LogError(task.Exception, "步骤17-子任务[{name}]异常", name);
        else if (task.IsCanceled)
            logger.LogWarning("步骤17-子任务[{name}]已取消", name);
        else
            logger.LogInformation("步骤17-子任务[{name}]完成", name);
    }

    private static async Task RegisterWindowsNotificationAsync(ILogger logger)
    {
#if WINDOWS
        try
        {
            var handler = Ioc.Default.GetRequiredService<IPlatformNotificationHandler>();
            await handler.RegisterForNotifications();
            logger.LogInformation("Windows通知注册成功");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "注册Windows通知失败");
        }
#endif
    }

    private static async Task InitRustCoreAsync(ILogger logger)
    {
        try
        {
            NativeCore.Initialize();
            NativeCore.SetLogCallback(logger);
            NativeCore.ProtocolRouter = Ioc.Default.GetRequiredService<ProtocolRouter>();
            NativeCore.DeviceManager = Ioc.Default.GetRequiredService<IDeviceManager>();
            NativeCore.RegisterCallbacks();
            NativeCore.NetworkService = (NetworkService?)Ioc.Default.GetService<INetworkService>();
            NativeCore.HeartbeatProcessor = Ioc.Default.GetService<HeartbeatProcessor>();
            logger.LogInformation("Rust Core 初始化完成，回调已注册");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "初始化 Rust Core 失败");
        }
    }

    private static async Task StartLocalSocketRelayAsync(ILogger logger)
    {
        try
        {
            var socketLogger = Ioc.Default.GetRequiredService<ILogger>();
            LocalSocketRelayServer.SetLogger(socketLogger);
            LocalSocketRelayServer.Start();
            logger.LogInformation("LocalSocketRelayServer启动完成");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "启动LocalSocketRelayServer失败");
        }
    }

    private static async Task InitWorkerConfigAsync(ILogger logger)
    {
        try
        {
            var config = Ioc.Default.GetRequiredService<NotifyRelay.Worker.Configuration.WorkerConfiguration>();
            var settings = Ioc.Default.GetRequiredService<IGeneralSettingsService>();

            config.ControlMyMonitorPath = settings.ControlMyMonitorPath;
            config.SelectedMonitors = settings.SelectedMonitors;
            config.EnableMonitorBrightnessSync = settings.EnableMonitorBrightnessSync;
            config.DynamicLightingBrightness = settings.DynamicLightingBrightness;
            config.DynamicLightingColor = settings.DynamicLightingColor;
            config.DynamicLightingEffect = settings.DynamicLightingEffect;
            config.EnableAutoRGB = settings.EnableAutoRGB;
            config.AutoRGBUpdateInterval = settings.AutoRGBUpdateInterval;

            logger.LogInformation("Worker 服务配置已初始化");

            // 按已持久化的设置开关，在应用启动时自动拉起对应的 Worker 服务
            await StartWorkerServicesAsync(logger, settings);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "初始化 Worker 服务配置失败");
        }
    }

    /// <summary>
    /// 应用启动时，依据已开启的设置项自动启动对应的 Worker 服务。
    /// </summary>
    private static async Task StartWorkerServicesAsync(ILogger logger, IGeneralSettingsService settings)
    {
        // 动态光效依赖 WinRT UI 亲和 API（DeviceWatcher / LampArray），需在 UI 线程启动
        if (settings.EnableDynamicLighting)
        {
            try
            {
                var lightingService = Ioc.Default.GetRequiredService<NotifyRelay.Worker.Services.DynamicLightingService>();
                await RunOnUiThreadAsync(lightingService.Initialize);
                logger.LogInformation("动态光效服务已按设置自动启动");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "自动启动动态光效服务失败");
            }
        }

        if (settings.EnableMonitorBrightnessSync)
        {
            try
            {
                var brightnessService = Ioc.Default.GetRequiredService<NotifyRelay.Worker.Services.MonitorBrightnessService>();
                brightnessService.StartSync();
                logger.LogInformation("显示器亮度同步服务已按设置自动启动");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "自动启动显示器亮度同步服务失败");
            }
        }

        if (settings.EnableDeepSeekBalanceMonitor)
        {
            try
            {
                var deepSeekService = Ioc.Default.GetRequiredService<NotifyRelay.Worker.Services.DeepSeekBalanceService>();
                deepSeekService.StartPolling();
                logger.LogInformation("DeepSeek 余额监控服务已按设置自动启动");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "自动启动 DeepSeek 余额监控服务失败");
            }
        }
    }

    /// <summary>
    /// 将操作分发到 UI 线程执行并等待其完成。
    /// </summary>
    private static Task RunOnUiThreadAsync(Action action)
    {
        var dispatcher = App.MainWindow?.DispatcherQueue;
        if (dispatcher is null)
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource();
        dispatcher.TryEnqueue(() =>
        {
            try
            {
                action();
            }
            catch
            {
                // 异常交由 StartWorkerServicesAsync 内的 try/catch 记录
            }
            finally
            {
                tcs.TrySetResult();
            }
        });
        return tcs.Task;
    }

    private static async Task InitAudioRelayAsync(ILogger logger)
    {
        try
        {
            var audioRelayService = Ioc.Default.GetRequiredService<DeviceCtrl.AudioRelay.AudioRelayService>();
            logger.LogInformation("音频中继服务已就绪");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "初始化音频中继服务失败");
        }
    }

    private static async Task StartLocalNotificationListenerAsync(ILogger logger)
    {
        try
        {
            var localListener = Ioc.Default.GetRequiredService<ILocalNotificationListenerService>();
            localListener.Start();
            logger.LogInformation("本地通知监听服务启动完成");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "启动本地通知监听服务失败");
        }
    }

    public static IHost BuildHost()
    {
        return new HostBuilder()
            .UseContentRoot(AppContext.BaseDirectory)
            .ConfigureHostConfiguration(config =>
            {
#if DEBUG
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Environment"] = Environments.Development
                });
#endif
            })
            .ConfigureAppConfiguration((context, config) =>
            {
                config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);
                if (context.HostingEnvironment.IsDevelopment())
                    config.AddJsonFile("appsettings.development.json", optional: true, reloadOnChange: false);
            })
            .ConfigureServices((context, services) =>
            {
                services.AddLocalization();
                ConfigureServices(services);
            })
            .UseSerilog((context, config) =>
            {
                config
                    .MinimumLevel.Debug()
                    .WriteTo.Debug(outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                    .WriteTo.File(
                        Path.Combine(ApplicationData.Current.LocalFolder.Path, "Logs", "Log_.log"),
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 7,
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
                    );
            })
            .Build();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ILogger>(sp => sp.GetRequiredService<ILogger<App>>())

        // Settings Services
        .AddSingleton<UserSettingsService>()
        .AddSingleton<IUserSettingsService>(sp => sp.GetRequiredService<UserSettingsService>())
        .AddSingleton<IGeneralSettingsService>(sp => sp.GetRequiredService<UserSettingsService>().GeneralSettingsService)
        .AddSingleton<IOverlaySettings>(sp => (IOverlaySettings)sp.GetRequiredService<UserSettingsService>().GeneralSettingsService)
        .AddSingleton<NotifyRelay.Worker.Configuration.IDeepSeekBalanceSettings, DeepSeekBalanceSettingsAccessor>()

        // Database and Repositories
        .AddSingleton<DatabaseContext>()
        .AddSingleton<DeviceRepository>()
        .AddSingleton<RemoteAppRepository>()
        .AddSingleton<NotificationRepository>()
        .AddSingleton<FilterConfigRepository>()

        // Platform-specific services
        .AddWindowsServices()
        // Services
        // 1. 首先注册基础服务
        .AddSingleton<ISystemInfoService, SystemInfoService>()
        .AddSingleton<IDeviceManager, DeviceManager>()
        .AddSingleton<IAdbService, AdbService>()
        .AddSingleton<IScreenMirrorService, ScreenMirrorService>()
        .AddSingleton<IFileTransferService, FileTransferService>()
        .AddSingleton<IProtocolSender, ProtocolSender>()
        .AddSingleton<IClipboardService, ClipboardService>()
        .AddSingleton<IRemoteAppService, RemoteAppService>()

        // 3. 注册ProtocolRouter
#if WINDOWS
        // 在Windows平台上，ProtocolRouter需要NetworkDriveMapper
        .AddSingleton<Func<NetworkDriveMapper>>(sp => () => sp.GetRequiredService<NetworkDriveMapper>())
#endif
        .AddSingleton<ProtocolRouter>()
        .AddSingleton<HeartbeatProcessor>()

        // 4. 注册INetworkService和工厂函数，它依赖ProtocolRouter
        .AddSingleton<INetworkService, NetworkService>()
        .AddSingleton<Func<INetworkService>>(sp => () => sp.GetRequiredService<INetworkService>())

        // 5. 注册ISessionManager，由INetworkService实现
        .AddSingleton<ISessionManager>(sp => (ISessionManager)sp.GetRequiredService<INetworkService>())

        // 6. 注册INotificationService，它依赖ISessionManager
        .AddSingleton<INotificationService, NotificationService>()
        .AddSingleton<Func<INotificationService>>(sp => () => sp.GetRequiredService<INotificationService>())
        .AddSingleton<ILocalNotificationListenerService, LocalNotificationListenerService>()

        // 注册通知过滤服务
        .AddSingleton<BackendRemoteFilter>()

        // 注册其他需要的工厂
        .AddSingleton<Func<IClipboardService>>(sp => () => sp.GetRequiredService<IClipboardService>())
        .AddSingleton<Func<IRemoteAppService>>(sp => () => sp.GetRequiredService<IRemoteAppService>())
        .AddSingleton<Func<IPlaybackService>>(sp => () => sp.GetRequiredService<IPlaybackService>())

        // 7. 注册IDiscoveryService，它依赖INetworkService
        .AddSingleton<IDiscoveryService, DiscoveryService>()

        // Worker Services
        .AddSingleton<NotifyRelay.Worker.Configuration.WorkerConfiguration>()
        .AddSingleton<NotifyRelay.Worker.Services.DeepSeekBalanceService>()
        .AddSingleton<NotifyRelay.Worker.Services.MonitorBrightnessService>()
        .AddSingleton<NotifyRelay.Worker.Services.DynamicLightingService>()

        // Audio Relay Service
        .AddSingleton<DeviceCtrl.AudioRelay.AudioRelayService>()

        // Overlay Render Service
        .AddSingleton<OverlayRenderService>()

        // 叠加层功能：登记后由 OverlayRenderService 主初始化按各自开关自动引导，
        // 新增功能只需在此追加一行登记，无需改动启动流程
        .AddSingleton<IOverlayFeature, KeyboardOverlayFeature>()
        .AddSingleton<IOverlayFeature, LogiBatteryOverlayFeature>()
        .AddSingleton<IOverlayFeature, HeartRateOverlayFeature>()

        // Heart Rate BLE Service
        .AddSingleton<NotifyRelay.Services.HeartRate.HeartRateBleService>()
        .AddSingleton<ViewModels.Settings.HeartRateViewModel>()

        // 罗技电池（LogiBattery）Provider 和 ViewModel
        .AddSingleton<LogiBatteryProvider>()
        .AddSingleton<ILogiBatteryProvider>(sp => sp.GetRequiredService<LogiBatteryProvider>())
        .AddSingleton<ViewModels.Settings.LogiBatteryViewModel>()

        // 时间浮窗（Clock）ViewModel
        .AddSingleton<ViewModels.Settings.ClockViewModel>()

        // ViewModels
        .AddSingleton<MainPageViewModel>()
        .AddSingleton<DevicesViewModel>()
        .AddSingleton<AppsViewModel>()
        .AddSingleton<LocalNotificationHistoryViewModel>();
    }

    /// <summary>
    /// Shows exception on the Debug Output.
    /// </summary>
    public static void HandleAppUnhandledException(Exception? ex)
    {
        Ioc.Default.GetService<ILogger>()?.LogCritical("Unhandled exception {ex}", ex);
    }

    public static async Task HandleStartupTaskAsync(bool enable)
    {
#if WINDOWS
        var startupTask = await StartupTask.GetAsync("8B5D3E3F-9B69-4E8A-A9F7-BFCA793B9AF0");

        if (enable)
        {
            if (startupTask.State == StartupTaskState.Disabled)
                await startupTask.RequestEnableAsync();
        }
        else
        {
            if (startupTask.State == StartupTaskState.Enabled)
                startupTask.Disable();
        }
#endif
    }
}
