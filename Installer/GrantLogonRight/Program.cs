// Grants "Log on as a service" (SeServiceLogonRight) to an account via the
// same LSA API the Services GUI uses (LsaAddAccountRights) when you set a
// service's logon account by hand. sc.exe create skips that step entirely —
// this replaces an earlier attempt at reproducing it by hand-patching a
// secedit-exported INF template, which turned out too fragile to debug
// remotely (encoding mismatches, missing sections, opaque exit codes with an
// empty log). Direct LSA calls fail with a specific, documented Win32 error
// instead.
//
// Usage: GrantLogonRight.exe <DOMAIN\account>
// Exit codes: 0 = granted, non-zero = see stderr message.

using System.Runtime.InteropServices;
using System.Security.Principal;

if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
{
    Console.Error.WriteLine("usage: GrantLogonRight.exe <DOMAIN\\account>");
    return 2;
}

string account = args[0];

byte[] sid;
try
{
    var identifier = (SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier));
    sid = new byte[identifier.BinaryLength];
    identifier.GetBinaryForm(sid, 0);
}
catch (Exception ex)
{
    // ex.Message is localized (Cyrillic on a RU-locale box) and can come out
    // mojibake once redirected through cmd.exe's OEM codepage — the type
    // name alone (e.g. IdentityNotMappedException) is enough to diagnose and
    // survives any codepage.
    Console.Error.WriteLine($"Account lookup failed for '{account}': {ex.GetType().Name}");
    return 3;
}

// One-shot CLI helper: the process exits right after this, so the OS
// reclaims the unmanaged buffer and LSA handle regardless — no try/finally
// needed for either.
IntPtr sidPtr = Marshal.AllocHGlobal(sid.Length);
Marshal.Copy(sid, 0, sidPtr, sid.Length);

var objectAttributes = default(NativeMethods.LSA_OBJECT_ATTRIBUTES);
uint status = NativeMethods.LsaOpenPolicy(
    IntPtr.Zero, ref objectAttributes, NativeMethods.POLICY_CREATE_ACCOUNT | NativeMethods.POLICY_LOOKUP_NAMES, out IntPtr policyHandle);
if (status != 0)
{
    Console.Error.WriteLine($"LsaOpenPolicy failed: {Win32MessageFor(status)}");
    return 4;
}

var rights = new[] { NativeMethods.ToLsaString("SeServiceLogonRight") };
status = NativeMethods.LsaAddAccountRights(policyHandle, sidPtr, rights, rights.Length);
NativeMethods.LsaClose(policyHandle);

if (status != 0)
{
    Console.Error.WriteLine($"LsaAddAccountRights failed: {Win32MessageFor(status)}");
    return 5;
}

Console.WriteLine($"OK: granted SeServiceLogonRight to {account}");
return 0;

static string Win32MessageFor(uint ntStatus)
{
    int win32Error = NativeMethods.LsaNtStatusToWinError(ntStatus);
    return $"Win32 error {win32Error}, NTSTATUS 0x{ntStatus:X8}";
}

internal static class NativeMethods
{
    public const int POLICY_CREATE_ACCOUNT = 0x0010;
    public const int POLICY_LOOKUP_NAMES = 0x0800;

    [StructLayout(LayoutKind.Sequential)]
    public struct LSA_UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LSA_OBJECT_ATTRIBUTES
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public int Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    public static LSA_UNICODE_STRING ToLsaString(string s)
    {
        return new LSA_UNICODE_STRING
        {
            Buffer = Marshal.StringToHGlobalUni(s),
            Length = (ushort)(s.Length * 2),
            MaximumLength = (ushort)((s.Length + 1) * 2),
        };
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern uint LsaOpenPolicy(
        IntPtr systemName, ref LSA_OBJECT_ATTRIBUTES objectAttributes, int desiredAccess, out IntPtr policyHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern uint LsaAddAccountRights(
        IntPtr policyHandle, IntPtr accountSid, LSA_UNICODE_STRING[] userRights, int countOfRights);

    [DllImport("advapi32.dll")]
    public static extern int LsaNtStatusToWinError(uint status);

    [DllImport("advapi32.dll")]
    public static extern uint LsaClose(IntPtr objectHandle);
}
