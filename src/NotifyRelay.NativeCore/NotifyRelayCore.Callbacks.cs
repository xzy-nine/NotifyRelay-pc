using System;
using System.Runtime.InteropServices;
using System.Text;

namespace NotifyRelay.Native;

public static partial class NotifyRelayCore
{
    // ======== Callback delegate types ========
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnPairingCb(IntPtr uuid, IntPtr messageType, IntPtr data, int intValue, IntPtr extra, IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnDataCb(IntPtr uuid, IntPtr messageType, IntPtr plaintext, IntPtr userData);

    // 状态查询回调（Rust 心跳线程锁外调用）：返回 0=不存在 / 1=存在无变更 / 2=存在有变更
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int OnStateQueryCb(IntPtr uuid, IntPtr featureId, int isMedia, IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnLogCb(int level, IntPtr message);

    /// <summary>
    /// TCP 扫描发现回调（Rust core 已完成自身过滤、名称解码与状态登记，
    /// 此处仅作为「设备状态有变化，请重新拉取快照」的信号）
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnDeviceDiscoveredCb(IntPtr uuid, IntPtr name, ushort port, int battery, IntPtr deviceType, IntPtr ip, IntPtr userData);

    // ======== Network callbacks ========
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnDeviceTimeoutCb(IntPtr uuid, IntPtr userData);

    // ======== Network callbacks ========
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnDeviceConnectedCb(IntPtr uuid, IntPtr ip, IntPtr userData);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnDeviceDisconnectedCb(IntPtr uuid, IntPtr userData);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnTcpErrorCb(IntPtr error, IntPtr userData);

    // ======== Callback setters ========
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void nrc_set_log_callback(IntPtr cb);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void nrc_set_on_pairing_cb(IntPtr ctx, OnPairingCb cb);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void nrc_set_on_data_cb(IntPtr ctx, OnDataCb cb);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void nrc_set_on_state_query_cb(IntPtr ctx, OnStateQueryCb cb);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void nrc_set_on_device_discovered_cb(IntPtr ctx, OnDeviceDiscoveredCb cb);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void nrc_set_on_device_timeout_cb(IntPtr ctx, OnDeviceTimeoutCb cb);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void nrc_set_on_device_connected_cb(IntPtr ctx, OnDeviceConnectedCb cb);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void nrc_set_on_device_disconnected_cb(IntPtr ctx, OnDeviceDisconnectedCb cb);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void nrc_set_on_tcp_error_cb(IntPtr ctx, OnTcpErrorCb cb);
}
