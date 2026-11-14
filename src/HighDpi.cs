using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
namespace SteamDirect {
 public sealed class DpiEntry : MarshalByRefObject { public void Run(string[] args) { Program.RunApplication(args); } public override object InitializeLifetimeService() { return null; } }
 internal static class HighDpi {
  [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
  internal static string ConfigureUserFallback(string directory = null) {
   string folder=directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SteamDirect");
   Directory.CreateDirectory(folder);string path=Path.Combine(folder,"high-dpi-v3.config");
   using(var source=Assembly.GetExecutingAssembly().GetManifestResourceStream("SteamDirect.HighDpiConfig")) {
    if(source==null)throw new InvalidOperationException("缺少高 DPI 配置资源。");
    string content;using(var reader=new StreamReader(source))content=reader.ReadToEnd();
    if(!File.Exists(path)||File.ReadAllText(path)!=content){string stage=path+".new-"+Guid.NewGuid().ToString("N");try{File.WriteAllText(stage,content);if(File.Exists(path))File.Replace(stage,path,null);else File.Move(stage,path);}finally{if(File.Exists(stage))File.Delete(stage);}}
   }
   return path;
  }
  [DllImport("user32.dll")]private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
  internal static void EnablePerMonitorContext() { try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch(EntryPointNotFoundException) { SetProcessDPIAware(); } }
  private static void RunUserConfigured(string[] args) {
   EnablePerMonitorContext();
   var setup=new AppDomainSetup { ApplicationBase=Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),ConfigurationFile=ConfigureUserFallback(),TargetFrameworkName=".NETFramework,Version=v4.8" };
   var domain=AppDomain.CreateDomain("SteamDirect.HighDpi.UI",null,setup);
   try { var entry=(DpiEntry)domain.CreateInstanceAndUnwrap(typeof(DpiEntry).Assembly.FullName,typeof(DpiEntry).FullName);entry.Run(args); }
   finally { AppDomain.Unload(domain); }
  }
  internal static bool EnsureConfiguration(string[] args) {
   string executable=Assembly.GetExecutingAssembly().Location;
   string path=executable+".config";
   if(File.Exists(path))return false;
   string stage=path+".new-"+Guid.NewGuid().ToString("N");
   try {
    using(var source=Assembly.GetExecutingAssembly().GetManifestResourceStream("SteamDirect.HighDpiConfig")) {
     if(source==null){SetProcessDPIAware();return false;}
     using(var output=new FileStream(stage,FileMode.CreateNew,FileAccess.Write,FileShare.None))source.CopyTo(output);
    }
    File.Move(stage,path);
    var start=new ProcessStartInfo(executable,Array.IndexOf(args,"--background")>=0?"--background":"") {UseShellExecute=false,CreateNoWindow=true};
    using(var process=Process.Start(start))return process!=null;
   }catch(IOException){if(!File.Exists(path)){RunUserConfigured(args);return true;}return false;}
    catch(UnauthorizedAccessException){RunUserConfigured(args);return true;}
    catch(Win32Exception){return false;}
   finally{try{if(File.Exists(stage))File.Delete(stage);}catch(IOException){}catch(UnauthorizedAccessException){}}
  }
 }
}