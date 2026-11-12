using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
namespace SteamDirect {
 internal static class HighDpi {
  [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
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
   }catch(IOException){if(!File.Exists(path))SetProcessDPIAware();return false;}
    catch(UnauthorizedAccessException){SetProcessDPIAware();return false;}
    catch(Win32Exception){return false;}
   finally{try{if(File.Exists(stage))File.Delete(stage);}catch(IOException){}catch(UnauthorizedAccessException){}}
  }
 }
}