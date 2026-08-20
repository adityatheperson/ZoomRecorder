using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using ZoomRecorder.App.Security;

namespace ZoomRecorder.App.Tests.Security;

public sealed class WindowsCredentialVaultTests
{
    [Fact]
    public async Task Save_writes_the_OpenAI_key_as_a_generic_credential_and_zeros_its_buffer()
    {
        var native = new FakeCredentialNativeApi();
        var vault = new WindowsCredentialVault(native);

        await vault.SaveApiKeyAsync("sk-test-value", CancellationToken.None);

        Assert.Equal("ZoomRecorder/OpenAI", native.WrittenTarget);
        Assert.Equal(NativeCredentialType.Generic, native.WrittenType);
        Assert.Equal(NativeCredentialPersistence.LocalMachine, native.WrittenPersistence);
        Assert.Equal("sk-test-value", native.WrittenSecret);
        Assert.True(native.SecretBufferWasZeroedBeforeRelease);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Save_rejects_blank_keys_before_calling_native_code(string? apiKey)
    {
        var native = new FakeCredentialNativeApi();
        var vault = new WindowsCredentialVault(native);

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            vault.SaveApiKeyAsync(apiKey!, CancellationToken.None));

        Assert.Equal(0, native.WriteCalls);
    }

    [Fact]
    public async Task Read_copies_the_key_and_releases_the_OS_owned_credential()
    {
        var native = new FakeCredentialNativeApi { ReadSecret = "sk-from-vault" };
        var vault = new WindowsCredentialVault(native);

        var result = await vault.GetApiKeyAsync(CancellationToken.None);

        Assert.Equal("sk-from-vault", result);
        Assert.Equal("ZoomRecorder/OpenAI", native.ReadTarget);
        Assert.Equal(NativeCredentialType.Generic, native.ReadType);
        Assert.Equal(1, native.FreeCalls);
    }

    [Fact]
    public async Task Read_returns_null_when_the_credential_does_not_exist()
    {
        var native = new FakeCredentialNativeApi { ReadError = WindowsCredentialVault.ErrorNotFound };
        var vault = new WindowsCredentialVault(native);

        var result = await vault.GetApiKeyAsync(CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, native.FreeCalls);
    }

    [Fact]
    public async Task Delete_is_idempotent_and_uses_the_exact_generic_target()
    {
        var native = new FakeCredentialNativeApi { DeleteError = WindowsCredentialVault.ErrorNotFound };
        var vault = new WindowsCredentialVault(native);

        await vault.DeleteApiKeyAsync(CancellationToken.None);

        Assert.Equal("ZoomRecorder/OpenAI", native.DeletedTarget);
        Assert.Equal(NativeCredentialType.Generic, native.DeletedType);
    }

    [Fact]
    public async Task Native_failures_preserve_only_the_error_code_and_operation()
    {
        const string secret = "sk-must-not-leak";
        var writeNative = new FakeCredentialNativeApi { WriteError = 5 };
        var readNative = new FakeCredentialNativeApi { ReadError = 5 };
        var deleteNative = new FakeCredentialNativeApi { DeleteError = 5 };

        var write = await Assert.ThrowsAsync<Win32Exception>(() =>
            new WindowsCredentialVault(writeNative).SaveApiKeyAsync(secret, CancellationToken.None));
        var read = await Assert.ThrowsAsync<Win32Exception>(() =>
            new WindowsCredentialVault(readNative).GetApiKeyAsync(CancellationToken.None));
        var delete = await Assert.ThrowsAsync<Win32Exception>(() =>
            new WindowsCredentialVault(deleteNative).DeleteApiKeyAsync(CancellationToken.None));

        Assert.All([write, read, delete], error =>
        {
            Assert.Equal(5, error.NativeErrorCode);
            Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("credential blob", error.Message, StringComparison.OrdinalIgnoreCase);
        });
        Assert.True(writeNative.SecretBufferWasZeroedBeforeRelease);
    }

    [Fact]
    public async Task Cancellation_is_observed_before_any_native_operation()
    {
        var native = new FakeCredentialNativeApi();
        var vault = new WindowsCredentialVault(native);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vault.GetApiKeyAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vault.SaveApiKeyAsync("sk-test", cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vault.DeleteApiKeyAsync(cancellation.Token));

        Assert.Equal(0, native.ReadCalls);
        Assert.Equal(0, native.WriteCalls);
        Assert.Equal(0, native.DeleteCalls);
    }

    private sealed class FakeCredentialNativeApi : ICredentialNativeApi
    {
        private nint readBlob;
        private nint readCredential;

        internal string? ReadSecret { get; init; }
        internal int WriteError { get; init; }
        internal int ReadError { get; init; }
        internal int DeleteError { get; init; }
        internal int WriteCalls { get; private set; }
        internal int ReadCalls { get; private set; }
        internal int DeleteCalls { get; private set; }
        internal int FreeCalls { get; private set; }
        internal string? WrittenTarget { get; private set; }
        internal NativeCredentialType WrittenType { get; private set; }
        internal NativeCredentialPersistence WrittenPersistence { get; private set; }
        internal string? WrittenSecret { get; private set; }
        internal string? ReadTarget { get; private set; }
        internal NativeCredentialType ReadType { get; private set; }
        internal string? DeletedTarget { get; private set; }
        internal NativeCredentialType DeletedType { get; private set; }
        internal bool SecretBufferWasZeroedBeforeRelease { get; private set; }
        public int LastError { get; private set; }

        public bool Write(ref NativeCredential credential, uint flags)
        {
            WriteCalls++;
            WrittenTarget = Marshal.PtrToStringUni(credential.TargetName);
            WrittenType = credential.Type;
            WrittenPersistence = credential.Persist;
            var bytes = new byte[checked((int)credential.CredentialBlobSize)];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            WrittenSecret = Encoding.Unicode.GetString(bytes);
            LastError = WriteError;
            return WriteError == 0;
        }

        public bool Read(string target, NativeCredentialType type, uint flags, out nint credential)
        {
            ReadCalls++;
            ReadTarget = target;
            ReadType = type;
            LastError = ReadError;
            if (ReadError != 0)
            {
                credential = nint.Zero;
                return false;
            }

            var bytes = Encoding.Unicode.GetBytes(ReadSecret ?? string.Empty);
            readBlob = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, readBlob, bytes.Length);
            var value = new NativeCredential
            {
                Type = NativeCredentialType.Generic,
                CredentialBlobSize = checked((uint)bytes.Length),
                CredentialBlob = readBlob
            };
            readCredential = Marshal.AllocHGlobal(Marshal.SizeOf<NativeCredential>());
            Marshal.StructureToPtr(value, readCredential, false);
            credential = readCredential;
            return true;
        }

        public bool Delete(string target, NativeCredentialType type, uint flags)
        {
            DeleteCalls++;
            DeletedTarget = target;
            DeletedType = type;
            LastError = DeleteError;
            return DeleteError == 0;
        }

        public void Free(nint credential)
        {
            Assert.Equal(readCredential, credential);
            FreeCalls++;
            Marshal.FreeHGlobal(readBlob);
            Marshal.FreeHGlobal(readCredential);
            readBlob = nint.Zero;
            readCredential = nint.Zero;
        }

        public void FreeSecret(nint secret)
        {
            var byteCount = Encoding.Unicode.GetByteCount(WrittenSecret ?? string.Empty);
            SecretBufferWasZeroedBeforeRelease = Enumerable.Range(0, byteCount)
                .All(index => Marshal.ReadByte(secret, index) == 0);
            Marshal.FreeHGlobal(secret);
        }
    }
}
