using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ZoomRecorder.Core.Processing;

namespace ZoomRecorder.App.Security;

internal sealed class WindowsCredentialVault : ICredentialVault
{
    internal const string TargetName = "ZoomRecorder/OpenAI";
    internal const int ErrorNotFound = 1168;
    private const string UserName = "ZoomRecorder";
    private readonly ICredentialNativeApi native;

    internal WindowsCredentialVault() : this(new CredentialNativeApi()) { }

    internal WindowsCredentialVault(ICredentialNativeApi native) =>
        this.native = native ?? throw new ArgumentNullException(nameof(native));

    public Task<string?> GetApiKeyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!native.Read(TargetName, NativeCredentialType.Generic, 0, out var pointer))
        {
            var error = native.LastError;
            if (error == ErrorNotFound)
            {
                return Task.FromResult<string?>(null);
            }

            throw Failure(error, "read");
        }

        if (pointer == nint.Zero)
        {
            throw new InvalidDataException("Windows Credential Manager returned an invalid OpenAI credential.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.Type != NativeCredentialType.Generic ||
                credential.CredentialBlob == nint.Zero ||
                credential.CredentialBlobSize == 0 ||
                credential.CredentialBlobSize > int.MaxValue ||
                credential.CredentialBlobSize % sizeof(char) != 0)
            {
                throw new InvalidDataException("Windows Credential Manager returned an invalid OpenAI credential.");
            }

            var bytes = new byte[checked((int)credential.CredentialBlobSize)];
            try
            {
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                var apiKey = Encoding.Unicode.GetString(bytes);
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    throw new InvalidDataException("Windows Credential Manager returned an invalid OpenAI credential.");
                }

                return Task.FromResult<string?>(apiKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            native.Free(pointer);
        }
    }

    public Task SaveApiKeyAsync(string apiKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var secretBytes = Encoding.Unicode.GetBytes(apiKey);
        nint target = nint.Zero;
        nint userName = nint.Zero;
        nint secret = nint.Zero;
        try
        {
            target = Marshal.StringToHGlobalUni(TargetName);
            userName = Marshal.StringToHGlobalUni(UserName);
            secret = Marshal.AllocHGlobal(secretBytes.Length);
            Marshal.Copy(secretBytes, 0, secret, secretBytes.Length);
            var credential = new NativeCredential
            {
                Type = NativeCredentialType.Generic,
                TargetName = target,
                CredentialBlobSize = checked((uint)secretBytes.Length),
                CredentialBlob = secret,
                Persist = NativeCredentialPersistence.LocalMachine,
                UserName = userName
            };

            var succeeded = native.Write(ref credential, 0);
            var error = succeeded ? 0 : native.LastError;
            if (!succeeded)
            {
                throw Failure(error, "save");
            }

            return Task.CompletedTask;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
            if (secret != nint.Zero)
            {
                ZeroSecret(secret, secretBytes.Length);
                native.FreeSecret(secret);
            }
            if (userName != nint.Zero)
            {
                Marshal.FreeHGlobal(userName);
            }
            if (target != nint.Zero)
            {
                Marshal.FreeHGlobal(target);
            }
        }
    }

    public Task DeleteApiKeyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!native.Delete(TargetName, NativeCredentialType.Generic, 0))
        {
            var error = native.LastError;
            if (error != ErrorNotFound)
            {
                throw Failure(error, "delete");
            }
        }

        return Task.CompletedTask;
    }

    private static Win32Exception Failure(int error, string operation) =>
        new(error, $"Windows Credential Manager could not {operation} the OpenAI API key.");

    private static unsafe void ZeroSecret(nint secret, int byteCount) =>
        new Span<byte>((void*)secret, byteCount).Clear();
}

internal enum NativeCredentialType : uint
{
    Generic = 1
}

internal enum NativeCredentialPersistence : uint
{
    LocalMachine = 2
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeCredential
{
    internal uint Flags;
    internal NativeCredentialType Type;
    internal nint TargetName;
    internal nint Comment;
    internal long LastWritten;
    internal uint CredentialBlobSize;
    internal nint CredentialBlob;
    internal NativeCredentialPersistence Persist;
    internal uint AttributeCount;
    internal nint Attributes;
    internal nint TargetAlias;
    internal nint UserName;
}

internal interface ICredentialNativeApi
{
    int LastError { get; }

    bool Write(ref NativeCredential credential, uint flags);

    bool Read(string target, NativeCredentialType type, uint flags, out nint credential);

    bool Delete(string target, NativeCredentialType type, uint flags);

    void Free(nint credential);

    void FreeSecret(nint secret);
}

internal sealed class CredentialNativeApi : ICredentialNativeApi
{
    public int LastError => Marshal.GetLastWin32Error();

    public bool Write(ref NativeCredential credential, uint flags) => CredWrite(ref credential, flags);

    public bool Read(string target, NativeCredentialType type, uint flags, out nint credential) =>
        CredRead(target, type, flags, out credential);

    public bool Delete(string target, NativeCredentialType type, uint flags) => CredDelete(target, type, flags);

    public void Free(nint credential) => CredFree(credential);

    public void FreeSecret(nint secret) => Marshal.FreeHGlobal(secret);

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        NativeCredentialType type,
        uint flags,
        out nint credential);

    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, NativeCredentialType type, uint flags);

    [DllImport("Advapi32.dll")]
    private static extern void CredFree(nint credential);
}
