using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace SteamDirect
{
    internal static class SteamTrust
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct TrustFile
        {
            internal uint Size;
            internal string Path;
            internal IntPtr File, Subject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct TrustData
        {
            internal uint Size;
            internal IntPtr Policy, Sip;
            internal uint Ui, Revocation, Choice;
            internal IntPtr Info;
            internal uint State;
            internal IntPtr StateData;
            internal string Url;
            internal uint Flags, Context;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProviderCertificate
        {
            internal uint Size;
            internal IntPtr Context;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);
        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provider, uint signer, int counterSigner, uint index);
        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint index);

        internal static FileStream OpenVerified(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length < 3 || path[1] != ':' ||
                (path[2] != Path.DirectorySeparatorChar && path[2] != Path.AltDirectorySeparatorChar))
                throw new InvalidOperationException("Steam 启动路径必须是本地绝对路径。");
            path = Path.GetFullPath(path);
            if (!string.Equals(Path.GetFileName(path), "steam.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Steam 启动文件名称不匹配。");
            if (new DriveInfo(Path.GetPathRoot(path)).DriveType == DriveType.Network ||
                !FilePathSafety.NoReparseAncestors(path))
                throw new InvalidOperationException("Steam 路径不可访问，或包含网络盘或重解析路径。");

            var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                if (!FilePathSafety.IsSameFinalPath(handle.SafeFileHandle, path))
                    throw new InvalidOperationException("Steam 启动文件实际路径不匹配。");
                VerifyPublisher(path, handle);
                if (!string.Equals(FileVersionInfo.GetVersionInfo(path).OriginalFilename, "steam.exe", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Steam 原始文件身份不匹配，已阻止启动。");
                return handle;
            }
            catch { handle.Dispose(); throw; }
        }

        private static void VerifyPublisher(string path, FileStream handle)
        {
            var info = new TrustFile
            {
                Size = (uint)Marshal.SizeOf(typeof(TrustFile)),
                Path = path,
                File = handle.SafeFileHandle.DangerousGetHandle()
            };
            var pointer = Marshal.AllocHGlobal((int)info.Size);
            bool marshaled = false;
            try
            {
                Marshal.StructureToPtr(info, pointer, false);
                marshaled = true;
                var data = new TrustData
                {
                    Size = (uint)Marshal.SizeOf(typeof(TrustData)), Ui = 2, Choice = 1,
                    Info = pointer, State = 1, Flags = 0x1000
                };
                var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
                try
                {
                    int result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
                    if (result != 0) throw new InvalidOperationException("Steam 数字签名验证失败，已阻止启动（0x" + result.ToString("X8") + "）。");
                    // Read the actual primary signer returned by this verification.
                    IntPtr provider = data.StateData == IntPtr.Zero ? IntPtr.Zero : WTHelperProvDataFromStateData(data.StateData);
                    IntPtr signer = provider == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvSignerFromChain(provider, 0, 0, 0);
                    IntPtr certificate = signer == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvCertFromChain(signer, 0);
                    if (certificate == IntPtr.Zero) throw new InvalidOperationException("无法核验 Steam 发布者。");
                    var verified = (ProviderCertificate)Marshal.PtrToStructure(certificate, typeof(ProviderCertificate));
                    if (verified.Context == IntPtr.Zero) throw new InvalidOperationException("Steam 签名证书无效。");
                    using (var publisher = new X509Certificate2(verified.Context))
                    {
                        string name = publisher.GetNameInfo(X509NameType.SimpleName, false);
                        if (name != "Valve Corp." && name != "Valve Corporation" && name != "Valve Corp")
                            throw new InvalidOperationException("Steam 发布者不是受信任的 Valve，已阻止启动。");
                    }
                }
                finally
                {
                    data.State = 2;
                    WinVerifyTrust(new IntPtr(-1), ref action, ref data);
                }
            }
            finally
            {
                if (marshaled) Marshal.DestroyStructure(pointer, typeof(TrustFile));
                Marshal.FreeHGlobal(pointer);
            }
        }
    }
}
