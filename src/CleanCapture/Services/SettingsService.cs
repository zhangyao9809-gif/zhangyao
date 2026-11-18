using System;
using System.IO;
using System.Text.Json;
using CleanCapture.Models;

namespace CleanCapture.Services;

/// <summary>
/// 设置的读写：只做一件事，把 AppSettings 存成 JSON、再读回来。
/// 存哪里：程序旁边的 CleanCapture.settings.json。
/// 这样才是绿色软件：不写注册表、不放 C 盘深处，重装系统拷走文件夹就能接着用。
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public string SettingsPath { get; }

    public AppSettings Current { get; private set; }

    public SettingsService()
    {
        // AppContext.BaseDirectory 就是 exe 所在的文件夹。
        var folder = AppContext.BaseDirectory;
        SettingsPath = Path.Combine(folder, "CleanCapture.settings.json");
        Current = Load();
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded != null)
                {
                    loaded.Normalize();
                    Current = loaded;
                    return Current;
                }
            }
        }
        catch
        {
            // 设置文件坏了也不应该让程序起不来：用默认值，还能继续用。
            // 坏文件会留在原地，下次保存时会覆盖掉。
        }

        Current = new AppSettings();
        Current.Normalize();
        return Current;
    }

    public void Save()
    {
        Current.Normalize();
        var json = JsonSerializer.Serialize(Current, JsonOptions);
        File.WriteAllText(SettingsPath, json);
    }

    /// <summary>截图 / 录屏的成品文件名：时间 + 类型，不会互相覆盖。</summary>
    public static string MakeFileName(string kind, string extension)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        return $"CleanCapture-{kind}-{stamp}.{extension}";
    }
}
