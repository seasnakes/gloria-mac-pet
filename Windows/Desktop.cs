using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Forms=System.Windows.Forms;

namespace Gloria {
    internal static class Desktop {
        [StructLayout(LayoutKind.Sequential)] internal struct Box {public int Left,Top,Right,Bottom;}
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window,out Box box);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h,System.Text.StringBuilder s,int n);
        [StructLayout(LayoutKind.Sequential)] struct LastInput {public uint size,time;}
        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LastInput info);
        internal static bool Sleeping() {var d=new LastInput{size=8};return GetLastInputInfo(ref d) && unchecked((uint)Environment.TickCount-d.time)>180000;}
        static readonly uint pid=(uint)Process.GetCurrentProcess().Id;
        internal static bool Window(out Box box,out IntPtr h) {
            box=new Box(); h=GetForegroundWindow(); uint owner;
            GetWindowThreadProcessId(h,out owner);
            if(h==IntPtr.Zero || owner==pid || IsIconic(h))return false;
            var cls=new System.Text.StringBuilder(80);GetClassName(h,cls,80);
            return cls.ToString()!="Progman" && cls.ToString()!="WorkerW" && cls.ToString()!="Shell_TrayWnd" && GetWindowRect(h,out box) && box.Right-box.Left>220;
        }
    }

    // Read the playback device's peak meter. No microphone, audio recording or saved samples.
    [ComImport,Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class DeviceEnumerator {}
    [ComImport,Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface Devices {
        [PreserveSig]int EnumAudioEndpoints(int flow,uint state,out IntPtr result);
        void GetDefaultAudioEndpoint(int flow,int role,out Device device);
    }
    [ComImport,Guid("D666063F-1587-4E43-81F1-B948E807363F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface Device {void Activate(ref Guid id,uint context,IntPtr args,[MarshalAs(UnmanagedType.IUnknown)]out object result);}
    [ComImport,Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface PeakMeter {void GetPeakValue(out float value);}
    internal sealed class Sound : IDisposable {
        PeakMeter meter; DateTime refresh;
        public bool Playing() {
            try {
                if(DateTime.UtcNow>=refresh) {
                    Dispose(); refresh=DateTime.UtcNow.AddSeconds(8);
                    var devices=(Devices)new DeviceEnumerator(); Device device=null;
                    try {
                        devices.GetDefaultAudioEndpoint(0,1,out device);
                        var id=typeof(PeakMeter).GUID; object m;device.Activate(ref id,23,IntPtr.Zero,out m);meter=(PeakMeter)m;
                    } finally {if(device!=null)Marshal.ReleaseComObject(device);Marshal.ReleaseComObject(devices);}
                }
                if(meter!=null){float v;meter.GetPeakValue(out v);return v>0.016f;}
            } catch(COMException){Dispose();}
            return false;
        }
        public void Dispose(){if(meter!=null){Marshal.ReleaseComObject(meter);meter=null;}}
    }
}
