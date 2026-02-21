using System.Security.Cryptography;
using System.Text.Json;

namespace FileServerLibrary;

public interface IPlugin;

public interface ICommand
{
    byte[] Execute(User user, IStoragePlugin storage, Logger logger);
}

public interface ICryptoPlugin : IPlugin
{
    byte[] Encrypt(byte[]key, byte[] data);
    byte[] Decrypt(byte[]key, byte[] data, int idx);
}

public interface IDecoderPlugin : IPlugin
{
    ICommand Decode(Logger logger, byte[] data);
}

public interface IStoragePlugin : IPlugin
{
    byte[] Read(long key, string propertyName);
    void Write(long key, string propertyName, byte[] data);
}

public interface IUserProviderPlugin: IPlugin
{
    (User, int) GetUser(byte[] data);
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
    public readonly string Name;
    public readonly int Id;
    public readonly byte[] Key;
    
    public User(string name, int id, string keyFileName)
    {
        Name = name;
        Id = id;
        Key = File.ReadAllBytes(keyFileName);
    }
}

public sealed class ChaCha20CryptoPlugin: ICryptoPlugin
{
    public ChaCha20CryptoPlugin(ServerConfigurationParameters parameters)
    {
    }

    public byte[] Encrypt(byte[] key, byte[] data)
    {
        if (key.Length != 32) throw new Exception("Invalid encryption key");
        var dataKeyAndNonce = RandomNumberGenerator.GetBytes(32+12);
        var dataCipher = new ChaCha20(dataKeyAndNonce[..32], dataKeyAndNonce[32..]);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new ChaCha20(key, nonce);
        var encryptedKeyAndNonce = cipher.Encrypt(dataKeyAndNonce, 0, 12+32);
        var encryptedData = dataCipher.Encrypt(data, 0, data.Length);
        var final = new byte[nonce.Length + encryptedKeyAndNonce.Length + encryptedData.Length + 32];
        nonce.CopyTo(final, 0);
        encryptedKeyAndNonce.CopyTo(final, nonce.Length);
        encryptedData.CopyTo(final, nonce.Length + encryptedKeyAndNonce.Length);
        var hmac = new HMACSHA256(key);
        hmac.ComputeHash(final, 0, final.Length-32).CopyTo(final, final.Length - 32);
        return final;
    }

    public byte[] Decrypt(byte[] key, byte[] data, int idx)
    {
        if (key.Length != 32) throw new Exception("Invalid encryption key");
        if (data.Length < 32+32+12+12+1+idx)
            throw new Exception("Invalid response");
        var hmac = new HMACSHA256(key);
        var hash = hmac.ComputeHash(data, idx, data.Length - 32 - idx);
        if (!hash.SequenceEqual(data.AsSpan(data.Length - 32, 32)))
            throw new Exception("Invalid response hash");
        var keyAndNonceOffset = 12 + idx;
        var cipher = new ChaCha20(key, data[idx..keyAndNonceOffset]);
        var decryptedKeyAndNonce = cipher.Encrypt(data, keyAndNonceOffset, 12+32);
        cipher = new ChaCha20(decryptedKeyAndNonce[..32], decryptedKeyAndNonce[32..]);
        var dataOffset = keyAndNonceOffset + 32 + 12;
        return cipher.Encrypt(data, dataOffset, data.Length - dataOffset - 32);
    }
}
