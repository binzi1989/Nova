using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace NovaDesktop.Services;

public sealed class WindowsCredentialVault
{
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private readonly string _targetPrefix;

    public WindowsCredentialVault(string targetPrefix = "NOVA/Desktop")
    {
        _targetPrefix = targetPrefix.TrimEnd('/');
    }

    public void Write(string provider, string secret)
    {
        var target = GetTarget(provider);
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException("Cannot store an empty credential.");
        }

        var bytes = Encoding.UTF8.GetBytes(secret);
        if (bytes.Length > 2560)
        {
            throw new InvalidOperationException("Credential exceeds the Windows Credential Manager size limit.");
        }

        var blob = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = target,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredentialPersistLocalMachine,
                UserName = Environment.UserName,
                Comment = "NOVA Desktop model provider credential"
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows Credential Manager rejected the credential.");
            }
        }
        finally
        {
            if (bytes.Length > 0)
            {
                Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
                Array.Clear(bytes);
            }
            Marshal.FreeCoTaskMem(blob);
        }
    }

    public string? Read(string provider)
    {
        var target = GetTarget(provider);
        if (!CredRead(target, CredentialTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return null;
            }
            throw new Win32Exception(error, "Unable to read Windows Credential Manager.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return null;
            }
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            try
            {
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                Array.Clear(bytes);
            }
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public bool IsStored(string provider)
        => !string.IsNullOrWhiteSpace(Read(provider));

    public void Delete(string provider)
    {
        var target = GetTarget(provider);
        if (!CredDelete(target, CredentialTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw new Win32Exception(error, "Unable to delete Windows Credential Manager entry.");
            }
        }
    }

    private string GetTarget(string provider)
    {
        var normalized = provider.Equals("deepseek", StringComparison.OrdinalIgnoreCase)
            ? "deepseek"
            : provider.Equals("openai", StringComparison.OrdinalIgnoreCase)
                ? "openai"
                : throw new InvalidOperationException("Unsupported credential provider.");
        return $"{_targetPrefix}/{normalized}";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        uint type,
        uint flags,
        out IntPtr credentialPointer);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
