using System;
using System.IO;
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
        using (RSA rsa = new RSACng(2048))
        using (RandomNumberGenerator random = RandomNumberGenerator.Create())
        {
            CertificateRequest request = new CertificateRequest("CN=Oracle Backup Manager",
                rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
            DateTimeOffset now = DateTimeOffset.UtcNow;
            using (X509Certificate2 certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(5)))
            {
                byte[] token = new byte[32], pfx = certificate.Export(X509ContentType.Pfx);
                random.GetBytes(token);
                byte[] data = new byte[token.Length + pfx.Length];
                Buffer.BlockCopy(token, 0, data, 0, token.Length);
                Buffer.BlockCopy(pfx, 0, data, token.Length, pfx.Length);
                return data;
            }
        }
    }

    public void Dispose() { Certificate.Dispose(); }
}
#endif
