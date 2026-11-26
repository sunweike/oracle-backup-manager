using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

internal sealed class PairingKeys
{
    private const string Header = "OBM1:";
    private const int KeyLength = 32;
    internal readonly byte[] Token;
    private readonly byte[] fingerprint;

    internal PairingKeys(byte[] token, byte[] fingerprint)
    {
        if (token.Length != KeyLength || fingerprint.Length != KeyLength)
            throw new IOException("配对信息格式无效");
        Token = token;
        this.fingerprint = fingerprint;
    }

    internal static byte[] Fingerprint(X509Certificate certificate)
    {
        using (SHA256 hash = SHA256.Create()) return hash.ComputeHash(certificate.GetRawCertData());
    }

    internal bool Trusts(X509Certificate certificate)
    {
        if (certificate == null) return false;
        using (X509Certificate2 cert = new X509Certificate2(certificate))
        {
            DateTime now = DateTime.UtcNow;
            return now >= cert.NotBefore.ToUniversalTime() && now <= cert.NotAfter.ToUniversalTime() &&
                Wire.Equal(Fingerprint(cert), fingerprint);
        }
    }

    internal byte[] Encode()
    {
        byte[] data = new byte[KeyLength * 2];
        Buffer.BlockCopy(Token, 0, data, 0, KeyLength);
        Buffer.BlockCopy(fingerprint, 0, data, KeyLength, KeyLength);
        return data;
    }

    internal static PairingKeys Decode(byte[] data)
    {
        if (data.Length != KeyLength * 2) throw new IOException("配对信息格式无效");
        byte[] token = new byte[KeyLength], fingerprint = new byte[KeyLength];
        Buffer.BlockCopy(data, 0, token, 0, KeyLength);
        Buffer.BlockCopy(data, KeyLength, fingerprint, 0, KeyLength);
        return new PairingKeys(token, fingerprint);
    }

    internal void Export(string path)
    {
        File.WriteAllText(path, Header + Convert.ToBase64String(Encode()) + Environment.NewLine, Encoding.ASCII);
    }

    internal static PairingKeys Import(string path)
    {
        string value;
        using (FileStream stream = File.OpenRead(path))
        {
            if (stream.Length < 1 || stream.Length > 128) throw new IOException("配对文件大小无效");
            value = Encoding.ASCII.GetString(Wire.ReadExactly(stream, (int)stream.Length)).Trim();
        }
        if (!value.StartsWith(Header, StringComparison.Ordinal)) throw new IOException("配对文件格式无效");
        try { return Decode(Convert.FromBase64String(value.Substring(Header.Length))); }
        catch (FormatException) { throw new IOException("配对文件格式无效"); }
    }
}

internal static class PairingStore
{
    private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("OracleBackupManager.Pairing.v1");

    private static string ClientPath(string host)
    {
        using (SHA256 hash = SHA256.Create())
        {
            string name = Wire.Hex(hash.ComputeHash(Encoding.ASCII.GetBytes(host.ToLowerInvariant())));
            return Path.Combine(Preferences.Folder, "receivers", name + ".bin");
        }
    }

    internal static PairingKeys GetClient(string host)
    {
        string path = ClientPath(host);
        if (!File.Exists(path)) throw new IOException("该接受端尚未配对，请为当前地址导入配对文件。");
        return PairingKeys.Decode(Read(path));
    }

    internal static void SaveClient(string host, PairingKeys keys)
    {
        Write(ClientPath(host), keys.Encode(), true);
    }

    internal static byte[] Read(string path)
    {
        using (FileStream input = File.OpenRead(path))
        {
            if (input.Length <= 0 || input.Length > 65536) throw new IOException("本机配对设置文件无效");
            return ProtectedData.Unprotect(Wire.ReadExactly(input, (int)input.Length), Entropy, DataProtectionScope.CurrentUser);
        }
    }

    internal static void Write(string path, byte[] data, bool replace)
    {
        byte[] protectedBytes = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(protectedBytes, 0, protectedBytes.Length);
                file.Flush(true);
            }
            if (replace && File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

#if SERVER_BUILD
internal sealed class ReceiverIdentity : IDisposable
{
    private static readonly object Gate = new object();
    internal readonly X509Certificate2 Certificate;
    internal readonly PairingKeys Keys;

    private ReceiverIdentity(byte[] data)
    {
        if (data.Length <= 32) throw new IOException("接受端证书设置无效");
        byte[] token = new byte[32], pfx = new byte[data.Length - 32];
        Buffer.BlockCopy(data, 0, token, 0, token.Length);
        Buffer.BlockCopy(data, token.Length, pfx, 0, pfx.Length);
        Certificate = new X509Certificate2(pfx, (string)null, X509KeyStorageFlags.UserKeySet);
        Keys = new PairingKeys(token, PairingKeys.Fingerprint(Certificate));
    }

    internal static ReceiverIdentity LoadOrCreate()
    {
        string path = Path.Combine(Preferences.Folder, "receiver-identity.bin");
        lock (Gate)
        {
            if (!File.Exists(path))
            {
                byte[] data = Generate();
                try { PairingStore.Write(path, data, false); }
                catch (IOException) { if (!File.Exists(path)) throw; }
            }
            return new ReceiverIdentity(PairingStore.Read(path));
        }
    }

    private static byte[] Generate()
    {
        using (RandomNumberGenerator random = RandomNumberGenerator.Create())
        {
            byte[] token = new byte[32], pfx = CreateServerPfx();
            random.GetBytes(token);
            byte[] data = new byte[token.Length + pfx.Length];
            Buffer.BlockCopy(token, 0, data, 0, token.Length);
            Buffer.BlockCopy(pfx, 0, data, token.Length, pfx.Length);
            return data;
        }
    }

    private static unsafe byte[] CreateServerPfx()
    {
        const int rsaAesProvider = 24; // PROV_RSA_AES
        const string serverAuthentication = "1.3.6.1.5.5.7.3.1";
        CspParameters key = new CspParameters(rsaAesProvider);
        key.KeyContainerName = "OracleBackupManager-" + Guid.NewGuid().ToString("N");
        key.KeyNumber = (int)KeyNumber.Exchange;
        key.Flags = CspProviderFlags.NoPrompt;

        // CryptoAPI signs with the temporary CSP key. Export the PFX before disposing the key container.
        using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider(2048, key))
        {
            rsa.PersistKeyInCsp = false;
            CspKeyContainerInfo keyInfo = rsa.CspKeyContainerInfo;
            NativeKeyProvider provider = new NativeKeyProvider {
                Container = keyInfo.KeyContainerName, Provider = keyInfo.ProviderName,
                Type = keyInfo.ProviderType, KeySpec = (int)keyInfo.KeyNumber
            };
            NativeAlgorithm signature = new NativeAlgorithm { Oid = "1.2.840.113549.1.1.11" }; // sha256WithRSAEncryption
            byte[] subject = new X500DistinguishedName("CN=Oracle Backup Manager").RawData;
            X509Extension usage = new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true);
            X509Extension enhancedUsage = new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid(serverAuthentication) }, false);
            byte[] usageOid = Encoding.ASCII.GetBytes(usage.Oid.Value + "\0");
            byte[] enhancedUsageOid = Encoding.ASCII.GetBytes(enhancedUsage.Oid.Value + "\0");
            byte[] usageValue = usage.RawData, enhancedUsageValue = enhancedUsage.RawData;
            DateTime now = DateTime.UtcNow;
            NativeSystemTime start = new NativeSystemTime(now.AddDays(-1));
            NativeSystemTime end = new NativeSystemTime(now.AddYears(5));
            fixed (byte* subjectPtr = subject, usageOidPtr = usageOid, enhancedOidPtr = enhancedUsageOid,
                         usagePtr = usageValue, enhancedPtr = enhancedUsageValue)
            {
                NativeBlob name = new NativeBlob { Size = subject.Length, Bytes = subjectPtr };
                NativeExtension* entries = stackalloc NativeExtension[2];
                entries[0] = new NativeExtension {
                    Oid = usageOidPtr, Critical = 1,
                    Value = new NativeBlob { Size = usageValue.Length, Bytes = usagePtr }
                };
                entries[1] = new NativeExtension {
                    Oid = enhancedOidPtr, Critical = 0,
                    Value = new NativeBlob { Size = enhancedUsageValue.Length, Bytes = enhancedPtr }
                };
                NativeExtensions extensions = new NativeExtensions { Count = 2, Entries = entries };
                IntPtr context = CertCreateSelfSignCertificate(IntPtr.Zero, ref name, 0, ref provider,
                    ref signature, ref start, ref end, ref extensions);
                if (context == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    using (X509Certificate2 certificate = new X509Certificate2(context))
                        return certificate.Export(X509ContentType.Pfx);
                }
                finally { CertFreeCertificateContext(context); }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NativeBlob
    {
        internal int Size;
        internal byte* Bytes;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeKeyProvider
    {
        internal string Container, Provider;
        internal int Type, Flags, ParameterCount;
        internal IntPtr Parameters;
        internal int KeySpec;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct NativeAlgorithm
    {
        internal string Oid;
        internal NativeBlob Parameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NativeExtension
    {
        internal byte* Oid;
        internal int Critical;
        internal NativeBlob Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NativeExtensions
    {
        internal int Count;
        internal NativeExtension* Entries;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSystemTime
    {
        private ushort year, month, dayOfWeek, day, hour, minute, second, milliseconds;

        internal NativeSystemTime(DateTime time)
        {
            year = (ushort)time.Year;
            month = (ushort)time.Month;
            dayOfWeek = (ushort)time.DayOfWeek;
            day = (ushort)time.Day;
            hour = (ushort)time.Hour;
            minute = (ushort)time.Minute;
            second = (ushort)time.Second;
            milliseconds = (ushort)time.Millisecond;
        }
    }

    [DllImport("crypt32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CertCreateSelfSignCertificate(IntPtr keyHandle, ref NativeBlob subject,
        int flags, ref NativeKeyProvider provider, ref NativeAlgorithm signature, ref NativeSystemTime start,
        ref NativeSystemTime end, ref NativeExtensions extensions);

    [DllImport("crypt32.dll", ExactSpelling = true)]
    private static extern bool CertFreeCertificateContext(IntPtr context);

    public void Dispose() { Certificate.Dispose(); }
}
#endif
