namespace FileServerLibrary;

public class FileStorage: IKeyValueStorage
{
    private readonly string _baseFolder;
    private readonly int _keyDivider;
    private readonly Logger _logger;

    public FileStorage(Logger logger, ServerConfigurationParameters parameters)
    {
        _logger = logger;
        _baseFolder = parameters.GetStringParameter("storageBaseFolder");
        _keyDivider = parameters.GetIntParameter("storageKeyDivider");
    }

    public byte[]? Get(KeyValueStorageKey key)
    {
        var path = BuildPath(key);
        if (!File.Exists(path)) return null;
        return File.ReadAllBytes(path);
    }

    public void Set(KeyValueStorageKey key, byte[] value)
    {
        var path = BuildPath(key, true);
        File.WriteAllBytes(path, value);
    }

    public void Delete(KeyValueStorageKey key)
    {
        var path = BuildPath(key);
        File.Delete(path);
    }

    public List<KeyValueStorageKey> GetKeys()
    {
        return Directory
            .EnumerateDirectories(_baseFolder)
            .Select(Path.GetFileName)
            .Where(dbName => dbName != null)
            .SelectMany(dbName => Directory
                .EnumerateFiles(Path.Combine(_baseFolder, dbName!), "*", SearchOption.AllDirectories)
                .Select(fileName => BuildKeyValueStorageKey(dbName!, fileName)))
            .ToList();
    }

    private static KeyValueStorageKey BuildKeyValueStorageKey(string dbName, string fileName)
    {
        var propertyName = Path.GetExtension(fileName);
        propertyName = propertyName.Length != 0 ? propertyName[1..] : null;
        return new KeyValueStorageKey(dbName,
            int.Parse(Path.GetFileNameWithoutExtension(fileName)), propertyName);
    }

    private string GetFolderName(string dbName, int key) => Path.Combine(_baseFolder, dbName, key.ToString());

    private string BuildPath(KeyValueStorageKey key, bool createFolder = false)
    {
        var folder = GetFolderName(key.DbName, key.Key / _keyDivider);
        if (createFolder && !Directory.Exists(folder)) Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, key.ToString());
        return key.PropertyName != null ? path + "." + key.PropertyName : path;
    }
}