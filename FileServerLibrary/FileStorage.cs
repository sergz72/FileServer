using System.Xml.Linq;

namespace FileServerLibrary;

public sealed class FileStorage: IStoragePlugin
{
    private readonly bool _hasProperties;
    private readonly string _baseFolder;
    private readonly int _keyDivider;
    
    public FileStorage(ServerConfigurationParameters parameters)
    {
        _hasProperties = parameters.GetBoolParameterOrDefault("storageHasProperties", false);
        _baseFolder = parameters.GetStringParameter("baseFolder");
        _keyDivider = parameters.GetIntParameter("keyDivider");
    }

    public IEnumerable<(int, byte[])> Get(string dbName, int fromKey, int toKey, string? propertyName = null)
    {
        throw new NotImplementedException();
    }

    public int GetFileVersion(string dbName, int key, string? propertyName = null)
    {
        var path = BuildPath(dbName, key, propertyName);
        return GetFileVersion(path);
    }
    
    public byte[]? GetLast(string dbName, int key, out int factKey, string? propertyName = null)
    {
        throw new NotImplementedException();
    }

    public void Set(string dbName, Dictionary<int, byte[]> items, string? propertyName = null)
    {
        foreach (var (key, value) in items)
        {
            var path = BuildPath(dbName, key, propertyName);
            var version = File.Exists(path) ? GetFileVersion(path) + 1 : 1;
            Save(path, version, value);
        }
    }

    private static void Save(string path, int version, byte[] value)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        fs.Write(BitConverter.GetBytes(version), 0, 4);
        fs.Write(value, 0, value.Length);
    }

    private string BuildPath(string dbName, int key, string? propertyName)
    {
        var path = Path.Combine(_baseFolder, dbName, (key / _keyDivider).ToString(), key.ToString());
        return propertyName != null ? path + "." + propertyName : path;
    }
    
    private static int GetFileVersion(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        var data = new byte[4];
        if (fs.Read(data, 0, 4) != 4) throw new Exception("Can't read file version");
        return BitConverter.ToInt32(data);
    }
}
