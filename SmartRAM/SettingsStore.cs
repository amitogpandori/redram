using System.IO;
using System.Text.Json;
namespace SmartRAM;
public record SmartRamSettings(bool Enabled,bool StartWithWindows,bool AutoAnalyze,bool Notifications,bool SafetyMode);
public static class SettingsStore{
 static string Dir=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SmartRAM");
 static string FilePath=>Path.Combine(Dir,"settings.json");
 public static SmartRamSettings Load(){try{return File.Exists(FilePath)?JsonSerializer.Deserialize<SmartRamSettings>(File.ReadAllText(FilePath))??Defaults:Defaults;}catch{return Defaults;}}
 public static void Save(SmartRamSettings s){Directory.CreateDirectory(Dir);File.WriteAllText(FilePath,JsonSerializer.Serialize(s,new JsonSerializerOptions{WriteIndented=true}));}
 public static SmartRamSettings Defaults=>new(true,false,true,true,true);
 public static void SetStartup(bool enabled){
  using var k=Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
  if(enabled)k.SetValue("SmartRAM",$"\"{Environment.ProcessPath}\"");else k.DeleteValue("SmartRAM",false);
 }
}