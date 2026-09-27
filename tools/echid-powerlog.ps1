# Combined EC + power log (read-only). Prints time, the power-source byte (0x0000 cmd 0x03 byte[7]),
# neighbour flags, and the live BatteryStatus so each EC value can be labelled.
param([int]$Seconds = 120, [int]$IntervalMs = 2000)

$cs = @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class HidLog {
    [StructLayout(LayoutKind.Sequential)] public struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] public struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }
    [StructLayout(LayoutKind.Sequential)] public struct HIDP_CAPS { public ushort Usage; public ushort UsagePage; public ushort InputReportByteLength; public ushort OutputReportByteLength; public ushort FeatureReportByteLength; [MarshalAs(UnmanagedType.ByValArray, SizeConst=17)] public ushort[] Reserved; public ushort NumberLinkCollectionNodes; public ushort NumberInputButtonCaps; public ushort NumberInputValueCaps; public ushort NumberInputDataIndices; public ushort NumberOutputButtonCaps; public ushort NumberOutputValueCaps; public ushort NumberOutputDataIndices; public ushort NumberFeatureButtonCaps; public ushort NumberFeatureValueCaps; public ushort NumberFeatureDataIndices; }
    [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll")] public static extern bool HidD_GetAttributes(IntPtr h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll")] public static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr p);
    [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr p);
    [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr p, out HIDP_CAPS c);
    [DllImport("hid.dll")] public static extern bool HidD_SetFeature(IntPtr h, byte[] b, int len);
    [DllImport("hid.dll")] public static extern bool HidD_GetFeature(IntPtr h, byte[] b, int len);
    [DllImport("setupapi.dll", CharSet=CharSet.Auto)] public static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr h, int f);
    [DllImport("setupapi.dll")] public static extern bool SetupDiEnumDeviceInterfaces(IntPtr d, IntPtr info, ref Guid g, int i, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", CharSet=CharSet.Auto)] public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr d, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, ref int needed, IntPtr info);
    [DllImport("setupapi.dll")] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr d);
    [DllImport("kernel32.dll", CharSet=CharSet.Auto, SetLastError=true)] public static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    const int DIGCF_PRESENT=0x02, DIGCF_DEVICEINTERFACE=0x10; const uint GENERIC_READ=0x80000000, GENERIC_WRITE=0x40000000, FILE_SHARE_READ=1, FILE_SHARE_WRITE=2, OPEN_EXISTING=3;
    public static string Find(int vid,int pid){ Guid g; HidD_GetHidGuid(out g); IntPtr set=SetupDiGetClassDevs(ref g,IntPtr.Zero,IntPtr.Zero,DIGCF_PRESENT|DIGCF_DEVICEINTERFACE);
        try { var did=new SP_DEVICE_INTERFACE_DATA(); did.cbSize=Marshal.SizeOf(did);
            for(int i=0;;i++){ if(!SetupDiEnumDeviceInterfaces(set,IntPtr.Zero,ref g,i,ref did)) break;
                int need=0; SetupDiGetDeviceInterfaceDetail(set,ref did,IntPtr.Zero,0,ref need,IntPtr.Zero); IntPtr buf=Marshal.AllocHGlobal(need);
                try { Marshal.WriteInt32(buf, IntPtr.Size==8?8:6); if(!SetupDiGetDeviceInterfaceDetail(set,ref did,buf,need,ref need,IntPtr.Zero)) continue;
                    string path=Marshal.PtrToStringAuto((IntPtr)(buf.ToInt64()+4)); IntPtr h=CreateFile(path,GENERIC_READ|GENERIC_WRITE,FILE_SHARE_READ|FILE_SHARE_WRITE,IntPtr.Zero,OPEN_EXISTING,0,IntPtr.Zero);
                    if(h==(IntPtr)(-1)) continue;
                    try { var at=new HIDD_ATTRIBUTES(); at.Size=Marshal.SizeOf(at); if(!HidD_GetAttributes(h,ref at)) continue; if(at.VendorID!=vid||at.ProductID!=pid) continue;
                        IntPtr pp; if(HidD_GetPreparsedData(h,out pp)){ var caps=new HIDP_CAPS(); HidP_GetCaps(pp,out caps); HidD_FreePreparsedData(pp); if(caps.FeatureReportByteLength==65) return path; } } finally { CloseHandle(h); }
                } finally { Marshal.FreeHGlobal(buf); } } } finally { SetupDiDestroyDeviceInfoList(set); } return null; }
    public static int ByteAt(string path,int feature,byte cmd,int idx){
        IntPtr h=CreateFile(path,GENERIC_READ|GENERIC_WRITE,FILE_SHARE_READ|FILE_SHARE_WRITE,IntPtr.Zero,OPEN_EXISTING,0,IntPtr.Zero);
        if(h==(IntPtr)(-1)) return -1;
        try { var send=new byte[65]; send[0]=0xA0; send[2]=0xA0; send[3]=(byte)feature; send[4]=(byte)(feature>>8); send[5]=cmd;
            HidD_SetFeature(h,send,65); System.Threading.Thread.Sleep(70); var get=new byte[65]; get[0]=0xA0;
            if(!HidD_GetFeature(h,get,65)) return -2; return get[idx];
        } finally { CloseHandle(h); }
    }
}
'@
Add-Type -TypeDefinition $cs -Language CSharp
$path=[HidLog]::Find(0x1025,0x174B); if(-not $path){ Write-Host "device not found"; return }
Write-Host ("{0,-9} {1,-7} {2,-7} {3,-7} {4,-7} {5,-7} {6}" -f "time","cmd3[7]","cmd2[7]","cmd6[7]","Online","Charg","Rate")
$end=(Get-Date).AddSeconds($Seconds)
while((Get-Date) -lt $end){
    $c3=[HidLog]::ByteAt($path,0x0000,0x03,7)
    $c2=[HidLog]::ByteAt($path,0x0000,0x02,7)
    $c6=[HidLog]::ByteAt($path,0x0000,0x06,7)
    $b=Get-CimInstance -Namespace root/WMI -ClassName BatteryStatus
    Write-Host ("{0:HH:mm:ss}  {1,-7} {2,-7} {3,-7} {4,-7} {5,-7} {6}" -f (Get-Date), ("0x{0:X2}" -f $c3), ("0x{0:X2}" -f $c2), ("0x{0:X2}" -f $c6), $b.PowerOnline, $b.Charging, $b.ChargeRate)
    Start-Sleep -Milliseconds $IntervalMs
}
Write-Host "done."
