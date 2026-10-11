using System.Runtime.InteropServices;
using System.Text;
using V6Meter.Core.Campus;

namespace V6Meter.App;

/// <summary>
/// The campus account in Windows Credential Manager (a generic credential, encrypted per user by Windows;
/// visible under Control Panel → Credential Manager → Windows Credentials). Verified: an elevated process of
/// the same user reads what a non-elevated one wrote, so autostart (elevated) and normal runs share it.
/// </summary>
internal static class CampusCredentials
{
    private const string Target = "v6meter/campus";
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;

    public static CampusAccount? Load()
    {
        if (!CredReadW(Target, CRED_TYPE_GENERIC, 0, out var p))
            return null;
        try
        {
            var c = Marshal.PtrToStructure<CREDENTIAL>(p);
            string password = c.CredentialBlobSize > 0 ? Marshal.PtrToStringUni(c.CredentialBlob, c.CredentialBlobSize / 2) : "";
            return string.IsNullOrEmpty(c.UserName) ? null : new CampusAccount(c.UserName, password);
        }
        finally { CredFree(p); }
    }

    /// <exception cref="System.ComponentModel.Win32Exception">Windows refused to store it.</exception>
    public static void Save(CampusAccount account)
    {
        byte[] blob = Encoding.Unicode.GetBytes(account.Password);
        IntPtr p = Marshal.AllocHGlobal(Math.Max(1, blob.Length));
        try
        {
            Marshal.Copy(blob, 0, p, blob.Length);
            var c = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = Target,
                UserName = account.Account,
                Comment = "v6meter：自动登录校园网",
                CredentialBlob = p,
                CredentialBlobSize = blob.Length,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
            };
            if (!CredWriteW(ref c, 0))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.Copy(new byte[blob.Length], 0, p, blob.Length); // do not leave the password in freed memory
            Marshal.FreeHGlobal(p);
            Array.Clear(blob);
        }
    }

    public static void Delete()
    {
        if (!CredDeleteW(Target, CRED_TYPE_GENERIC, 0) && Marshal.GetLastWin32Error() != ERROR_NOT_FOUND)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWriteW(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDeleteW(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
