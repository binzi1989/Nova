using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nova.Maimai.Connector;

internal sealed class EncryptedVault
{
    private static readonly byte[] Magic = "NOVAMM01"u8.ToArray();
    private static readonly byte[] Entropy = "NOVA:Maimai:Vault:v1"u8.ToArray();
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };
    private readonly string _root;
    private readonly string _keyPath;
    private readonly string _vaultPath;
    private readonly string _mutexName;

    public EncryptedVault(string? root = null)
    {
        _root = Path.GetFullPath(root
            ?? Environment.GetEnvironmentVariable("NOVA_MAIMAI_DATA_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NOVA", "Connectors", "Maimai"));
        _keyPath = Path.Combine(_root, "vault.key");
        _vaultPath = Path.Combine(_root, "profiles.vault");
        _mutexName = $"Local\\Nova.Maimai.Vault.{ProfilePolicy.Sha256(_root)[..24]}";
    }

    public string Root => _root;
    public string VaultPath => _vaultPath;
    public string KeyPath => _keyPath;

    public T Read<T>(Func<IReadOnlyList<ProfileRecord>, T> action)
        => WithLock(() => action(Load().Profiles));

    public T Update<T>(Func<List<ProfileRecord>, T> action)
        => WithLock(() =>
        {
            var envelope = Load();
            var result = action(envelope.Profiles);
            Save(envelope);
            return result;
        });

    private T WithLock<T>(Func<T> action)
    {
        Directory.CreateDirectory(_root);
        using var mutex = new Mutex(false, _mutexName);
        var entered = false;
        try
        {
            try
            {
                entered = mutex.WaitOne(TimeSpan.FromSeconds(15));
            }
            catch (AbandonedMutexException)
            {
                entered = true;
            }

            if (!entered)
            {
                throw new TimeoutException("加密仓正忙，请稍后重试。");
            }
            return action();
        }
        finally
        {
            if (entered)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private VaultEnvelope Load()
    {
        if (!File.Exists(_vaultPath))
        {
            return new VaultEnvelope();
        }

        var bytes = File.ReadAllBytes(_vaultPath);
        var minimumLength = Magic.Length + 12 + 16 + 1;
        if (bytes.Length < minimumLength || !bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException("脉脉连接器数据仓格式无效或已损坏。");
        }

        var nonce = bytes.AsSpan(Magic.Length, 12);
        var tag = bytes.AsSpan(Magic.Length + 12, 16);
        var ciphertext = bytes.AsSpan(Magic.Length + 28);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(GetOrCreateKey(), 16);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, Magic);
        return JsonSerializer.Deserialize<VaultEnvelope>(plaintext, _json)
               ?? throw new InvalidDataException("脉脉连接器数据仓内容为空。");
    }

    private void Save(VaultEnvelope envelope)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(envelope, _json);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ciphertext = new byte[plaintext.Length];
        using (var aes = new AesGcm(GetOrCreateKey(), 16))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, Magic);
        }

        var bytes = new byte[Magic.Length + nonce.Length + tag.Length + ciphertext.Length];
        Magic.CopyTo(bytes, 0);
        nonce.CopyTo(bytes, Magic.Length);
        tag.CopyTo(bytes, Magic.Length + nonce.Length);
        ciphertext.CopyTo(bytes, Magic.Length + nonce.Length + tag.Length);

        Directory.CreateDirectory(_root);
        var temporary = _vaultPath + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, _vaultPath, true);
    }

    private byte[] GetOrCreateKey()
    {
        if (File.Exists(_keyPath))
        {
            return Dpapi.Unprotect(File.ReadAllBytes(_keyPath), Entropy);
        }

        Directory.CreateDirectory(_root);
        var key = RandomNumberGenerator.GetBytes(32);
        var protectedKey = Dpapi.Protect(key, Entropy);
        var temporary = _keyPath + ".tmp";
        File.WriteAllBytes(temporary, protectedKey);
        File.Move(temporary, _keyPath, true);
        return key;
    }

    private static class Dpapi
    {
        private const int CryptProtectUiForbidden = 0x1;

        public static byte[] Protect(byte[] data, byte[] entropy)
            => Transform(data, entropy, protect: true);

        public static byte[] Unprotect(byte[] data, byte[] entropy)
            => Transform(data, entropy, protect: false);

        private static byte[] Transform(byte[] data, byte[] entropy, bool protect)
        {
            var input = Blob.FromBytes(data);
            var optionalEntropy = Blob.FromBytes(entropy);
            try
            {
                var success = protect
                    ? CryptProtectData(ref input, null, ref optionalEntropy, IntPtr.Zero, IntPtr.Zero,
                        CryptProtectUiForbidden, out var output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, ref optionalEntropy, IntPtr.Zero, IntPtr.Zero,
                        CryptProtectUiForbidden, out output);
                if (!success)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                try
                {
                    var bytes = new byte[output.Length];
                    Marshal.Copy(output.Data, bytes, 0, output.Length);
                    return bytes;
                }
                finally
                {
                    LocalFree(output.Data);
                }
            }
            finally
            {
                input.Free();
                optionalEntropy.Free();
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Blob
        {
            public int Length;
            public IntPtr Data;

            public static Blob FromBytes(byte[] bytes)
            {
                var blob = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
                Marshal.Copy(bytes, 0, blob.Data, bytes.Length);
                return blob;
            }

            public void Free()
            {
                if (Data != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(Data);
                    Data = IntPtr.Zero;
                }
            }
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(
            ref Blob dataIn, string? description, ref Blob optionalEntropy, IntPtr reserved,
            IntPtr promptStruct, int flags, out Blob dataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(
            ref Blob dataIn, IntPtr description, ref Blob optionalEntropy, IntPtr reserved,
            IntPtr promptStruct, int flags, out Blob dataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
