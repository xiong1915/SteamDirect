using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
namespace SteamDirect {
 internal static class SteamTrust {
  [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct FileInfo {internal uint Size;internal string Path;internal IntPtr File,Subject;}
  [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct TrustData {internal uint Size;internal IntPtr Policy,Sip;internal uint Ui,Revocation,Choice;internal IntPtr Info;internal uint State;internal IntPtr StateData;internal string Url;internal uint Flags,Context;}
  [DllImport("wintrust.dll",ExactSpelling=true,CharSet=CharSet.Unicode)]private static extern int WinVerifyTrust(IntPtr window,ref Guid action,ref TrustData data);
  internal static FileStream OpenVerified(string path) {
   path=Path.GetFullPath(path);if(!string.Equals(System.IO.Path.GetFileName(path),"steam.exe",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Steam 启动文件名称不匹配。");
   var handle=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
   try {
    var info=new FileInfo{Size=(uint)Marshal.SizeOf(typeof(FileInfo)),Path=path,File=handle.SafeFileHandle.DangerousGetHandle()};var pointer=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FileInfo)));
    try {
     Marshal.StructureToPtr(info,pointer,false);var data=new TrustData{Size=(uint)Marshal.SizeOf(typeof(TrustData)),Ui=2,Choice=1,Info=pointer,State=1,Flags=0x1000};var action=new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
     int result;try{result=WinVerifyTrust(new IntPtr(-1),ref action,ref data);}finally{data.State=2;WinVerifyTrust(new IntPtr(-1),ref action,ref data);}
     if(result!=0)throw new InvalidOperationException("Steam 数字签名验证失败，已阻止启动（0x"+result.ToString("X8")+"）。");
    }finally{Marshal.DestroyStructure(pointer,typeof(FileInfo));Marshal.FreeHGlobal(pointer);}
    using(var signed=X509Certificate.CreateFromSignedFile(path))
    using(var certificate=new X509Certificate2(signed)) {
     string publisher=certificate.GetNameInfo(X509NameType.SimpleName,false);
     if(publisher!="Valve Corp."&&publisher!="Valve Corporation"&&publisher!="Valve Corp")throw new InvalidOperationException("Steam 发布者不是受信任的 Valve，已阻止启动。");
    }
    if (!string.Equals(System.Diagnostics.FileVersionInfo.GetVersionInfo(path).OriginalFilename, "steam.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Steam 原始文件身份不匹配，已阻止启动。");
    return handle;
   }catch{handle.Dispose();throw;}
  }
 }
}