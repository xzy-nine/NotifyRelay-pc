using System;
using System.Runtime.InteropServices;
using System.Text;

namespace NotifyRelay.Native;

public static partial class NotifyRelayCore
{
    // ======== Core start (统一启动 TCP、心跳、离线检测、发送队列、扫描、重连、mDNS) ========
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern long nrc_start_core(IntPtr ctx, IntPtr uuid, IntPtr name, int battery, IntPtr deviceType, ushort tcpPort, IntPtr pubKey, ulong heartbeatIntervalMs, long offlineTimeoutSec, ulong offlineCheckIntervalMs, ulong reconnectIntervalSecs, uint reconnectMaxRetries);

    // ======== Heartbeat scheduler params (电量/名称变化) ========
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void nrc_update_heartbeat_scheduler_params(IntPtr ctx, IntPtr name, int battery, IntPtr deviceType);

    // ======== Device state snapshot ========
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr nrc_get_device_list(IntPtr ctx, long authedTimeoutMs, long unauthedTimeoutMs);

    // ======== Network layer ========
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nrc_remove_device_session(IntPtr ctx, IntPtr uuid);

    // ======== Network change ========
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void nrc_on_network_changed(IntPtr ctx, IntPtr localIp);

    // ======== Local IP ========
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr nrc_get_local_ip();

    // ======== Filter ========
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nrc_set_filter_config(IntPtr ctx, IntPtr configJson);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr nrc_map_local_package(IntPtr ctx, IntPtr pkg);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nrc_check_filter_mode(IntPtr ctx, IntPtr mappedPkg, IntPtr originalPkg, IntPtr title, IntPtr text);

    // ======== Other utilities ========
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr nrc_compute_dedup_key(IntPtr deviceUuid, IntPtr data);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr nrc_compute_feature_id(IntPtr superPkg, IntPtr paramV2Raw, IntPtr title, IntPtr text, IntPtr instanceId);

    // ======== FTP credentials ========
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr nrc_derive_ftp_credentials(IntPtr sharedSecretB64);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr nrc_derive_password_hash(IntPtr password);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr nrc_generate_random_password();

    // ======== Dedup unified ========
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nrc_dedup(IntPtr ctx, int action, IntPtr dedupKey, long arg1Ms, long arg2Ms);

    public static partial class Safe
    {
        // ======== Network wrappers ========
        public static long StartCore(IntPtr ctx, string uuid, string name, int battery, string deviceType, ushort tcpPort, string pubKey, ulong heartbeatIntervalMs, long offlineTimeoutSec, ulong offlineCheckIntervalMs, ulong reconnectIntervalSecs, uint reconnectMaxRetries)
        {
            var u = StringToPtr(uuid); var n = StringToPtr(name); var d = StringToPtr(deviceType); var pk = StringToPtr(pubKey);
            var result = NotifyRelayCore.nrc_start_core(ctx, u, n, battery, d, tcpPort, pk, heartbeatIntervalMs, offlineTimeoutSec, offlineCheckIntervalMs, reconnectIntervalSecs, reconnectMaxRetries);
            Marshal.FreeHGlobal(u); Marshal.FreeHGlobal(n); Marshal.FreeHGlobal(d); Marshal.FreeHGlobal(pk);
            return result;
        }

        public static int RemoveDeviceSession(IntPtr ctx, string uuid)
        {
            var u = StringToPtr(uuid);
            var result = NotifyRelayCore.nrc_remove_device_session(ctx, u);
            Marshal.FreeHGlobal(u);
            return result;
        }

        // ======== Heartbeat scheduler params ========
        public static void UpdateHeartbeatSchedulerParams(IntPtr ctx, string name, int battery, string deviceType)
        {
            var n = StringToPtr(name); var d = StringToPtr(deviceType);
            NotifyRelayCore.nrc_update_heartbeat_scheduler_params(ctx, n, battery, d);
            Marshal.FreeHGlobal(n); Marshal.FreeHGlobal(d);
        }

        public static string? GetDeviceList(IntPtr ctx, long authedTimeoutMs, long unauthedTimeoutMs)
        {
            return PtrToStringAndFree(NotifyRelayCore.nrc_get_device_list(ctx, authedTimeoutMs, unauthedTimeoutMs));
        }

        // ======== Filter ========
        public static int SetFilterConfig(IntPtr ctx, string configJson)
        {
            var json = StringToPtr(configJson);
            var result = NotifyRelayCore.nrc_set_filter_config(ctx, json);
            Marshal.FreeHGlobal(json);
            return result;
        }

        public static string? MapLocalPackage(IntPtr ctx, string pkg)
        {
            var p = StringToPtr(pkg);
            var result = NotifyRelayCore.nrc_map_local_package(ctx, p);
            Marshal.FreeHGlobal(p);
            return PtrToStringAndFree(result);
        }

        public static int CheckFilterMode(IntPtr ctx, string mappedPkg, string originalPkg, string title, string text)
        {
            var mp = StringToPtr(mappedPkg); var op = StringToPtr(originalPkg); var t = StringToPtr(title); var tx = StringToPtr(text);
            var result = NotifyRelayCore.nrc_check_filter_mode(ctx, mp, op, t, tx);
            Marshal.FreeHGlobal(mp); Marshal.FreeHGlobal(op); Marshal.FreeHGlobal(t); Marshal.FreeHGlobal(tx);
            return result;
        }

        // ======== Utility wrappers ========
        public static string? ComputeDedupKey(string deviceUuid, string data)
        {
            var u = StringToPtr(deviceUuid); var d = StringToPtr(data);
            var result = NotifyRelayCore.nrc_compute_dedup_key(u, d);
            Marshal.FreeHGlobal(u); Marshal.FreeHGlobal(d);
            return PtrToStringAndFree(result);
        }

        public static string? ComputeFeatureId(string superPkg, string paramV2Raw, string title, string text, string instanceId)
        {
            var pkg = StringToPtr(superPkg); var param = StringToPtr(paramV2Raw); var t = StringToPtr(title);
            var tx = StringToPtr(text); var iid = StringToPtr(instanceId);
            var result = NotifyRelayCore.nrc_compute_feature_id(pkg, param, t, tx, iid);
            Marshal.FreeHGlobal(pkg); Marshal.FreeHGlobal(param); Marshal.FreeHGlobal(t); Marshal.FreeHGlobal(tx); Marshal.FreeHGlobal(iid);
            return PtrToStringAndFree(result);
        }

        // ======== FTP ========
        public static string? DeriveFtpCredentials(string sharedSecretB64)
        {
            var s = StringToPtr(sharedSecretB64);
            var result = NotifyRelayCore.nrc_derive_ftp_credentials(s);
            Marshal.FreeHGlobal(s);
            return PtrToStringAndFree(result);
        }

        public static string? DerivePasswordHash(string password)
        {
            var p = StringToPtr(password);
            var result = NotifyRelayCore.nrc_derive_password_hash(p);
            Marshal.FreeHGlobal(p);
            return PtrToStringAndFree(result);
        }

        public static string? GenerateRandomPassword()
        {
            var result = NotifyRelayCore.nrc_generate_random_password();
            return PtrToStringAndFree(result);
        }

        // ======== Dedup unified ========
        public static int Dedup(IntPtr ctx, int action, string dedupKey, long arg1Ms, long arg2Ms)
        {
            var k = StringToPtr(dedupKey);
            var result = NotifyRelayCore.nrc_dedup(ctx, action, k, arg1Ms, arg2Ms);
            Marshal.FreeHGlobal(k);
            return result;
        }
    }
}
