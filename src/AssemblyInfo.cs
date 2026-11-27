[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: System.Reflection.AssemblyVersion(SteamDirect.AppVersion.Number)]
[assembly: System.Reflection.AssemblyFileVersion(SteamDirect.AppVersion.Number)]
[assembly: System.Reflection.AssemblyProduct("SteamDirect")]

namespace SteamDirect
{
    internal static class AppVersion
    {
        // Synchronized from app.manifest by Build.ps1.
        internal const string Number = "3.0.46.0";
        internal static readonly string Label = "v" + new System.Version(Number).ToString(new System.Version(Number).Revision == 0 ? 3 : 4);
    }
}
