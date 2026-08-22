using System.Runtime.InteropServices;
using System.Text;
using ClapBack.Core.Abstractions;

namespace ClapBack.Windows.Infrastructure;

public sealed class WindowsCredentialStore : ICredentialStore
{
    private const uint Generic = 1;
    private const uint PersistLocalMachine = 2;

    public Task<string?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CredRead(name, Generic, 0, out var credential))
        {
            return Task.FromResult<string?>(null);
        }

        try
        {
            var nativeCredential = Marshal.PtrToStructure<NativeCredential>(credential);
            if (nativeCredential.CredentialBlob == IntPtr.Zero || nativeCredential.CredentialBlobSize == 0)
            {
                return Task.FromResult<string?>(null);
            }

            var bytes = new byte[checked((int)nativeCredential.CredentialBlobSize)];
            Marshal.Copy(nativeCredential.CredentialBlob, bytes, 0, bytes.Length);
            return Task.FromResult<string?>(Encoding.UTF8.GetString(bytes));
        }
        finally
        {
            CredFree(credential);
        }
    }

    public Task SetAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = Encoding.UTF8.GetBytes(value);
        var target = Marshal.StringToCoTaskMemUni(name);
        var blob = Marshal.AllocCoTaskMem(bytes.Length);
        var userName = Marshal.StringToCoTaskMemUni(Environment.UserName);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = Generic,
                TargetName = target,
                CredentialBlob = blob,
                CredentialBlobSize = (uint)bytes.Length,
                Persist = PersistLocalMachine,
                UserName = userName
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new InvalidOperationException($"无法保存 Windows 凭据，错误码：{Marshal.GetLastWin32Error()}");
            }

        }
        finally
        {
            Marshal.FreeCoTaskMem(target);
            Marshal.FreeCoTaskMem(blob);
            Marshal.FreeCoTaskMem(userName);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CredDelete(name, Generic, 0);
        return Task.CompletedTask;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref NativeCredential userCredential, uint flags);

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credential);

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("Advapi32.dll")]
    private static extern void CredFree(IntPtr credential);
}
