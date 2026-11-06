using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Glint.Core;

/// <summary>
/// File access in folders the signed-in user can write (%APPDATA%\Glint: config, plugins, layout backups, status).
/// Glint runs elevated, so a plain File.WriteAllText there would follow a junction / symbolic link the user (or any
/// program running as the user) planted and write, read or delete anywhere as administrator. Every such access runs
/// impersonating a de-elevated copy of our own token (Administrators deny-only, no privileges, medium integrity): the
/// file system then checks it exactly as if the user had done it, so a redirected path can reach only what the user
/// could reach anyway. Not used for the install folder (trusted-plugins.json), which needs the elevated token.
/// </summary>
public static class UserFs
{
    /// <summary>Tests only: extra group SIDs to switch to deny-only, to prove that file access really goes through
    /// the restricted token without needing an elevated test process.</summary>
    internal static string[]? TestDisableSids
    {
        get => _testSids;
        set { lock (_lock) { _testSids = value; _token?.Dispose(); _token = null; _tried = false; } }
    }
    static string[]? _testSids;
    static SafeAccessTokenHandle? _token;
    static bool _tried;
    static readonly object _lock = new();

    public static T Run<T>(Func<T> f)
    {
        var t = Token();
        return t == null ? f() : WindowsIdentity.RunImpersonated(t, f);
    }

    public static void Run(Action a) => Run(() => { a(); return 0; });

    public static string ReadAllText(string path) => Run(() => File.ReadAllText(path));
    public static byte[] ReadAllBytes(string path) => Run(() => File.ReadAllBytes(path));
    public static bool Exists(string path) => Run(() => File.Exists(path));
    public static void Delete(string path) => Run(() => File.Delete(path));
    public static void CreateDirectory(string path) => Run(() => { Directory.CreateDirectory(path); });

    /// <summary>Write via a temp file + rename, so readers never see half a file.</summary>
    public static void WriteAllTextAtomic(string path, string text) => Run(() =>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path + ".tmp", text);
        File.Move(path + ".tmp", path, true);
    });

    public static void WriteAllText(string path, string text) => Run(() => File.WriteAllText(path, text));

    /// <summary>True when the restricted token is in use (the selftest asserts this).</summary>
    public static bool Active => Token() != null;

    /// <summary>The identity file access runs as: (Administrators not usable?, integrity RID).</summary>
    public static (bool adminDenyOnly, int integrityRid) Describe() => Run(() =>
    {
        using var id = WindowsIdentity.GetCurrent();
        bool admin = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); // false for a deny-only group
        return (!admin, IntegrityRid(id.AccessToken));
    });

    static SafeAccessTokenHandle? Token()
    {
        lock (_lock)
        {
            if (_tried) return _token;
            _tried = true;
            try { _token = Create(); } catch { _token = null; }
            return _token;
        }
    }

    const uint TOKEN_QUERY = 0x8, TOKEN_DUPLICATE = 0x2, TOKEN_ASSIGN_PRIMARY = 0x1, TOKEN_ADJUST_DEFAULT = 0x80, TOKEN_IMPERSONATE = 0x4;
    const uint DISABLE_MAX_PRIVILEGE = 0x1;
    const int TokenIntegrityLevel = 25;
    const uint SE_GROUP_INTEGRITY = 0x20;

    static SafeAccessTokenHandle Create()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY | TOKEN_DUPLICATE | TOKEN_ASSIGN_PRIMARY | TOKEN_ADJUST_DEFAULT | TOKEN_IMPERSONATE, out var proc))
            throw new System.ComponentModel.Win32Exception();
        using (proc)
        {
            var sids = new List<string> { "S-1-5-32-544", "S-1-5-114" }; // Administrators; local account and member of Administrators
            if (_testSids != null) sids.AddRange(_testSids);
            var mem = new List<IntPtr>();
            try
            {
                var arr = new SID_AND_ATTRIBUTES[sids.Count];
                for (int i = 0; i < sids.Count; i++)
                {
                    var bin = new byte[SecurityIdentifier.MaxBinaryLength];
                    var sid = new SecurityIdentifier(sids[i]);
                    sid.GetBinaryForm(bin, 0);
                    var p = Marshal.AllocHGlobal(sid.BinaryLength);
                    Marshal.Copy(bin, 0, p, sid.BinaryLength);
                    mem.Add(p);
                    arr[i] = new SID_AND_ATTRIBUTES { Sid = p };
                }
                if (!CreateRestrictedToken(proc, DISABLE_MAX_PRIVILEGE, (uint)arr.Length, arr, 0, IntPtr.Zero, 0, IntPtr.Zero, out var restricted))
                    throw new System.ComponentModel.Win32Exception();
                // medium integrity (only ever lowers: raising fails and the current level stays)
                var medium = new SecurityIdentifier("S-1-16-8192");
                var mb = new byte[medium.BinaryLength]; medium.GetBinaryForm(mb, 0);
                var mp = Marshal.AllocHGlobal(mb.Length); Marshal.Copy(mb, 0, mp, mb.Length); mem.Add(mp);
                if (IntegrityRid(restricted) > 0x2000)
                {
                    var label = new SID_AND_ATTRIBUTES { Sid = mp, Attributes = SE_GROUP_INTEGRITY };
                    int size = Marshal.SizeOf<SID_AND_ATTRIBUTES>() + mb.Length;
                    if (!SetTokenInformation(restricted, TokenIntegrityLevel, ref label, (uint)size))
                    {
                        restricted.Dispose();
                        throw new System.ComponentModel.Win32Exception();
                    }
                }
                return restricted;
            }
            finally { foreach (var p in mem) Marshal.FreeHGlobal(p); }
        }
    }

    static int IntegrityRid(SafeAccessTokenHandle t)
    {
        GetTokenInformation(t, TokenIntegrityLevel, IntPtr.Zero, 0, out uint len);
        if (len == 0) return -1;
        var buf = Marshal.AllocHGlobal((int)len);
        try
        {
            if (!GetTokenInformation(t, TokenIntegrityLevel, buf, len, out _)) return -1;
            var sid = Marshal.ReadIntPtr(buf);
            int n = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
            return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(n - 1)));
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    [StructLayout(LayoutKind.Sequential)] struct SID_AND_ATTRIBUTES { public IntPtr Sid; public uint Attributes; }
    [DllImport("kernel32")] static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32", SetLastError = true)]
    static extern bool CreateRestrictedToken(SafeAccessTokenHandle existing, uint flags, uint disableCount, SID_AND_ATTRIBUTES[] disable,
        uint deleteCount, IntPtr deletePrivs, uint restrictCount, IntPtr restrictSids, out SafeAccessTokenHandle newToken);
    [DllImport("advapi32", SetLastError = true)] static extern bool SetTokenInformation(SafeAccessTokenHandle t, int cls, ref SID_AND_ATTRIBUTES info, uint len);
    [DllImport("advapi32", SetLastError = true)] static extern bool GetTokenInformation(SafeAccessTokenHandle t, int cls, IntPtr info, uint len, out uint ret);
    [DllImport("advapi32")] static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32")] static extern IntPtr GetSidSubAuthority(IntPtr sid, uint n);
}
