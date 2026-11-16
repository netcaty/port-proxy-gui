namespace PortProxyManager.Services;

/// <summary>界面偏好，存在数据目录的 settings.json。</summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public int WindowWidth { get; set; } = 1120;
    public int WindowHeight { get; set; } = 660;
    public int WindowX { get; set; } = int.MinValue;
    public int WindowY { get; set; } = int.MinValue;
    public bool Maximized { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool TrayTipShown { get; set; }
    public string LastGroup { get; set; } = string.Empty;
    public string LastFilter { get; set; } = string.Empty;
    public Dictionary<string, int> ColumnWidths { get; set; } = new();
}

public sealed class SettingsStore
{
    private readonly string _file;
    public AppSettings Value { get; private set; }

    public SettingsStore(string file)
    {
        _file = file;
        Value = Load(file);
    }

    private static AppSettings Load(string file)
    {
        try
        {
            return JsonIo.Read<AppSettings>(file) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            LogService.Current?.Warn($"settings.json 解析失败，使用默认设置：{ex.Message}");
            return new AppSettings();
        }
    }

    /// <summary>保存失败只记日志，不打断用户操作。</summary>
    public void Save()
    {
        try
        {
            JsonIo.Write(_file, Value);
        }
        catch (Exception ex)
        {
            LogService.Current?.Warn($"保存设置失败：{ex.Message}");
        }
    }
}
