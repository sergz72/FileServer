using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace FileServerLibrary;

public interface IPlugin;

public record ServerParameters(
    ushort Port,
    string Name,
    string Handler,
    ServerConfigurationParameters Parameters,
    ICryptoPlugin? CryptoPlugin,
    IDecoderPlugin DecoderPlugin,
    IUserProviderPlugin? UserProvider,
    IStoragePlugin StoragePlugin,
    ILoggerCreator LoggerCreator
);

public interface IServerPlugin : IPlugin
{
    void Start();
    void Stop();
}

public interface IHandlerPlugin : IPlugin
{
    void Handle(byte[] data, IPEndPoint ep);
    void Handle(TcpClient client);
}

public interface ICommand
{
    byte[] Execute(User? user, IStoragePlugin storage, Logger logger);
}

public interface ICryptoPlugin : IPlugin
{
    byte[] Encrypt(byte[]key, byte[] data, int extraSpace);
    byte[] Decrypt(byte[]key, byte[] data, int idx, int length);
}

public interface IDecoderPlugin : IPlugin
{
    ICommand Decode(Logger logger, byte[] data);
}

public sealed record KeyValue(int Key, int Version, byte[] Value)
{
    public void ToBinary(BinaryWriter writer)
    {
        writer.Write(Version);
        writer.Write(Key);
        writer.Write(Value.Length);
        writer.Write(Value);
    }

    public static List<KeyValue> From(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        var result = new List<KeyValue>(length);
        while (length-- > 0)
        {
            var key = reader.ReadInt32();
            var valueLength = reader.ReadInt32();
            result.Add(new KeyValue(key, 0, reader.ReadBytes(valueLength)));
        }
        return result;
    }

    public byte[] BuildData(bool versioned)
    {
        if (!versioned)
            return Value;
        var data = new byte[Value.Length + 4];
        BitConverter.GetBytes(Version).CopyTo(data, 0);
        Value.CopyTo(data, 4);
        return data;
    }

    public static KeyValue ReadData(int key, byte[] data, bool versioned)
    {
        var version = versioned ? BitConverter.ToInt32(data, 0) : 0;
        return new KeyValue(key, version, versioned ? data[4..] : data);
    }

    public bool Equals(KeyValue? other)
    {
        if (other == null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Key == other.Key && Version == other.Version && Value.SequenceEqual(other.Value);
    }
}

public interface IStoragePlugin : IPlugin, IDisposable
{
    // database info and IEnumerable of KeyValue
    (DatabaseInfo, IEnumerable<KeyValue>) Get(string dbName, int from, int to, string? propertyName = null);
    (DatabaseInfo, int) GetFileVersion(string dbName, int key, string? propertyName = null);
    // database info and KeyValue
    (DatabaseInfo, KeyValue?) GetLast(string dbName, int from, int to, string? propertyName = null);
    DatabaseInfo Set(string dbName, int expectedVersion, List<KeyValue> data, string? propertyName = null);
    DatabaseInfo AddOrUpdate(string dbName, int key, string? propertyName, Func<byte[]> addFunc, Func<byte[], byte[]> updateFunc);
}

public interface IUserProviderPlugin: IPlugin
{
    int GetUser(byte[] data, out User user);
}

public sealed record ServerConfigurationParameters(
    Dictionary<string, Type> Plugins,
    Dictionary<string, JsonElement> PluginParameters
)
{
    public string GetStringParameter(string name)
    {
        return GetStringParameterOrNull(name) ?? throw new Exception($"parameter {name} not found");
    }

    public string? GetStringParameterOrNull(string name)
    {
        if (!PluginParameters.TryGetValue(name, out var element))
            return null;
        if (element.ValueKind != JsonValueKind.String) throw new Exception($"{name} is not string");
        return element.GetString()!;
    }

    public T GetParameter<T>(string name)
    {
        if (!PluginParameters.TryGetValue(name, out var element))
            throw new Exception($"parameter {name} not found");
        return element.Deserialize<T>() ?? throw new Exception($"{name} is not {typeof(T).Name}");
    }
    
    public bool GetBoolParameterOrDefault(string name, bool defaultValue)
    {
        if (!PluginParameters.TryGetValue(name, out var element))
            return defaultValue;
        if (element.ValueKind != JsonValueKind.True && element.ValueKind != JsonValueKind.False) throw new Exception($"{name} is not boolean");
        return element.GetBoolean();
    }

    public int GetIntParameter(string name)
    {
        if (!PluginParameters.TryGetValue(name, out var element))
            throw new Exception($"parameter {name} not found");
        if (element.ValueKind != JsonValueKind.Number) throw new Exception($"{name} is not number");
        return element.GetInt32();
    }
    
    public int GetIntParameterOrDefault(string name, int defaultValue)
    {
        if (!PluginParameters.TryGetValue(name, out var element))
            return defaultValue;
        if (element.ValueKind != JsonValueKind.Number) throw new Exception($"{name} is not number");
        return element.GetInt32();
    }
    
    public T CreateInstance<T>(string pluginName, params object?[]? args) where T : class
    {
        if (!Plugins.TryGetValue(pluginName, out var plugin)) throw new Exception($"plugin {pluginName} not found");
        return CreateInstance<T>(plugin, args);
    }
    
    private static T CreateInstance<T>(Type t, params object?[]? args) where T : class
    {
        return (Activator.CreateInstance(t, args)
                ?? throw new Exception($"cannot create instance for type {t.Name}")) as T
               ?? throw new Exception($"cannot cast {t.Name} to {typeof(T).Name}");
    }
}

public class User
{
    public const int HashSize = 32;
    
    public readonly string Name;
    public readonly int Id;
    public readonly byte[] Key;
    
    public User(string name, int id, string keyFileName)
    {
        Name = name;
        Id = id;
        Key = File.ReadAllBytes(keyFileName);
    }
    
    public int ValidateData(byte[] data, int encrypredDataIdx)
    {
        if (data.Length < HashSize+1+encrypredDataIdx)
            throw new Exception("ValidateData: too short response");
        var hmac = new HMACSHA256(Key);
        var length = data.Length - HashSize;
        var hash = hmac.ComputeHash(data, 0, length);
        if (!hash.SequenceEqual(data.AsSpan(length, HashSize)))
            throw new Exception("Invalid response hash");
        return length - encrypredDataIdx;
    }

    public void AddHash(byte[] encrypted)
    {
        var hmac = new HMACSHA256(Key);
        var length = encrypted.Length - HashSize;
        hmac.ComputeHash(encrypted, 0, length).CopyTo(encrypted, length);
    }
}

public sealed class ChaCha20CryptoPlugin: ICryptoPlugin
{
    public const int ExtraPayloadSize = 2 * ChaCha20.NonceLength + ChaCha20.KeyLength;
    
    public ChaCha20CryptoPlugin(ServerConfigurationParameters parameters)
    {
    }

    public byte[] Encrypt(byte[] key, byte[] data, int extraSpace)
    {
        if (key.Length != ChaCha20.KeyLength) throw new Exception("Invalid encryption key");
        var dataKeyAndNonce = RandomNumberGenerator.GetBytes(ChaCha20.KeyLength + ChaCha20.NonceLength);
        var dataCipher = new ChaCha20(dataKeyAndNonce[..ChaCha20.KeyLength], dataKeyAndNonce[ChaCha20.KeyLength..]);
        var nonce = RandomNumberGenerator.GetBytes(ChaCha20.NonceLength);

        var final = new byte[ExtraPayloadSize + data.Length + extraSpace];
        nonce.CopyTo(final, 0);
        
        var cipher = new ChaCha20(key, nonce);
        cipher.Encrypt(final, nonce.Length, dataKeyAndNonce, 0, ChaCha20.KeyLength + ChaCha20.NonceLength);
        dataCipher.Encrypt(final, ExtraPayloadSize, data, 0, data.Length);

        return final;
    }

    public byte[] Decrypt(byte[] key, byte[] data, int idx, int length)
    {
        if (key.Length != ChaCha20.KeyLength) throw new Exception("Invalid encryption key");
        if (length < ExtraPayloadSize + 1)
            throw new Exception("Invalid response");
        var keyAndNonceOffset = ChaCha20.NonceLength + idx;
        var cipher = new ChaCha20(key, data[idx..keyAndNonceOffset]);
        var decryptedKeyAndNonce = new byte[ChaCha20.KeyLength + ChaCha20.NonceLength];
        cipher.Encrypt(decryptedKeyAndNonce, 0, data, keyAndNonceOffset, ChaCha20.KeyLength + ChaCha20.NonceLength);
        cipher = new ChaCha20(decryptedKeyAndNonce[..ChaCha20.KeyLength], decryptedKeyAndNonce[ChaCha20.KeyLength..]);
        var dataOffset = keyAndNonceOffset + ChaCha20.KeyLength + ChaCha20.NonceLength;
        var result = new byte[length - ExtraPayloadSize];
        cipher.Encrypt(result, 0, data, dataOffset, result.Length);
        return result;
    }
}

public class DatabaseInfo
{
    protected readonly ReaderWriterLockSlim Lock = new();
    
    public readonly string DbName;
    
    public int DbVersion { get; private set; } = 1;
    
    internal virtual void EnterReadLock() => Lock.EnterReadLock();
    internal void EnterWriteLock() => Lock.EnterWriteLock();
    public virtual void ExitReadLock() => Lock.ExitReadLock();
    public void ExitWriteLock() => Lock.ExitWriteLock();
    
    public DatabaseInfo(string dbName)
    {
        DbName = dbName;
    }
    
    internal void CheckVersionAndIncrement(int expectedVersion)
    {
        if (expectedVersion != DbVersion) throw new Exception("Database version mismatch");
        DbVersion++;
    }

    public int GetVersionAndUnlock()
    {
        var version = DbVersion;
        ExitReadLock();
        return version;
    }
    
    public void IncrementVersion()
    {
        DbVersion++;
    }

    public virtual void Cleanup(bool enterWriteLock)
    {
    }
}
