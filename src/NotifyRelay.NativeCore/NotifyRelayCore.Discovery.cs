using System;
using System.Runtime.InteropServices;
using System.Text;

namespace NotifyRelay.Native;

public static partial class NotifyRelayCore
{
    // ======== Discovery ========
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void nrc_add_known_device(IntPtr ctx, IntPtr uuid, IntPtr ip);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void nrc_remove_known_device(IntPtr ctx, IntPtr uuid);

    public static partial class Safe
    {
        // ======== Discovery ========
        public static void AddKnownDevice(IntPtr ctx, string uuid, string ip)
        {
            var u = StringToPtr(uuid); var i = StringToPtr(ip);
            NotifyRelayCore.nrc_add_known_device(ctx, u, i);
            Marshal.FreeHGlobal(u); Marshal.FreeHGlobal(i);
        }

        public static void RemoveKnownDevice(IntPtr ctx, string uuid)
        {
            var u = StringToPtr(uuid);
            NotifyRelayCore.nrc_remove_known_device(ctx, u);
            Marshal.FreeHGlobal(u);
        }
    }
}
