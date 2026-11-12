using System.IO;
using System.Security.Cryptography;
using Microsoft.Win32;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace SmartRAM;
public enum FileSafety { Protected, Review, SafeToClean }
public record FileItem(string Path,long Bytes,string Category,FileSafety Safety,string Reason,DateTime CreatedAt,DateTime ModifiedAt,bool CloudOnly=false,string? DuplicateGroup=null,bool DuplicateKeeper=false,string? Owner=null,string? Ownership=null,string? SimilarGroup=null,int Similarity=0){ public string Extension=>System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant(); }
public record ScanProgress(long Files,long Bytes,long Folders,long Skipped,string Phase,string Current,double Percent);
public record StorageScanResult(string Root,IReadOnlyList<FileItem> Files,long TotalBytes,long Skipped){
 public IEnumerable<IGrouping<string,FileItem>> Groups=>Files.GroupBy(x=>x.Category).OrderByDescending(g=>g.Sum(x=>x.Bytes));
 public long SafeBytes=>Files.Where(x=>x.Safety==FileSafety.SafeToClean&&!x.CloudOnly).Sum(x=>x.Bytes);
 public long DuplicateRecoverableBytes=>Files.Where(x=>x.DuplicateGroup!=null&&!x.DuplicateKeeper).Sum(x=>x.Bytes);
}
public static class StorageScanner{
 record InstalledApp(string Name,string Root);
 static List<InstalledApp>? cachedApps;
 static DateTime cachedAppsAt;
 static readonly HashSet<string> Video=new(StringComparer.OrdinalIgnoreCase){".mp4",".mkv",".avi",".mov",".wmv",".webm",".m4v"};
 static readonly HashSet<string> Images=new(StringComparer.OrdinalIgnoreCase){".jpg",".jpeg",".png",".gif",".webp",".bmp",".heic",".tiff"};
 static readonly HashSet<string> Audio=new(StringComparer.OrdinalIgnoreCase){".mp3",".wav",".flac",".aac",".m4a",".ogg",".wma"};
 static readonly HashSet<string> Docs=new(StringComparer.OrdinalIgnoreCase){".pdf",".doc",".docx",".xls",".xlsx",".ppt",".pptx",".txt",".csv",".rtf"};
 static readonly HashSet<string> Archives=new(StringComparer.OrdinalIgnoreCase){".zip",".rar",".7z",".tar",".gz",".iso"};
 static readonly HashSet<string> Binaries=new(StringComparer.OrdinalIgnoreCase){
  ".exe",".dll",".sys",".msi",".msix",".msixbundle",".appx",".appxbundle",".ocx",".drv",".cpl",".scr",".com",
  ".bat",".cmd",".ps1",".jar",".node",".efi",".ax",".pyd",".vbs",".vbe",".js",".jse",".wsf",".wsh",".hta",".pif",".msp",".mst",".msc"
 };
 static string Category(string ext)=>Video.Contains(ext)?"Videos":Images.Contains(ext)?"Images":Audio.Contains(ext)?"Audio":Docs.Contains(ext)?"Documents":Archives.Contains(ext)?"Archives":"Other";
 static int KeeperScore(FileItem f){int s=0;var p=f.Path;if(f.CloudOnly)s-=40;if(f.Safety==FileSafety.SafeToClean)s-=50;if(p.Contains(@"\Temp\",StringComparison.OrdinalIgnoreCase))s-=40;if(p.Contains(@"\Downloads\",StringComparison.OrdinalIgnoreCase))s-=10;if(p.Contains(@"\Documents\",StringComparison.OrdinalIgnoreCase)||p.Contains(@"\Pictures\",StringComparison.OrdinalIgnoreCase)||p.Contains(@"\Videos\",StringComparison.OrdinalIgnoreCase)||p.Contains(@"\Music\",StringComparison.OrdinalIgnoreCase))s+=30;if(f.Owner is not null)s+=20;return s;}

 static string? TryFullPath(string? path){
  if(string.IsNullOrWhiteSpace(path))return null;
  try{return Path.GetFullPath(path.Trim().Trim('"'));}catch{return null;}
 }
 static string NormalizeDir(string path){
  var full=TryFullPath(path)??path;
  return full.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
 }
 static bool SameDir(string a,string b){
  try{return string.Equals(NormalizeDir(a),NormalizeDir(b),StringComparison.OrdinalIgnoreCase);}catch{return false;}
 }
 static bool Under(string path,string root){
  if(string.IsNullOrWhiteSpace(path)||string.IsNullOrWhiteSpace(root))return false;
  try{return NormalizeDir(path).StartsWith(NormalizeDir(root),StringComparison.OrdinalIgnoreCase);}catch{return false;}
 }
 static IEnumerable<string> TempRoots(){
  var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  void Add(string? p){var full=TryFullPath(p);if(full is null||!seen.Add(NormalizeDir(full)))return;}
  try{Add(Path.GetTempPath());}catch{}
  Add(Environment.GetEnvironmentVariable("TEMP"));
  Add(Environment.GetEnvironmentVariable("TMP"));
  var win=Environment.GetFolderPath(Environment.SpecialFolder.Windows);
  if(!string.IsNullOrWhiteSpace(win))Add(Path.Combine(win,"Temp"));
  foreach(var r in seen)yield return r;
 }
 static bool IsInPureTemp(string path)=>TempRoots().Any(t=>Under(path,t));
 static IEnumerable<string> AppDataRoots(){
  var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  void Add(string? p){var full=TryFullPath(p);if(full is null||!seen.Add(NormalizeDir(full)))return;}
  Add(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
  Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
  var profile=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
  if(!string.IsNullOrWhiteSpace(profile)){
   Add(Path.Combine(profile,"AppData"));
   Add(Path.Combine(profile,"AppData","Local"));
   Add(Path.Combine(profile,"AppData","Roaming"));
   Add(Path.Combine(profile,"AppData","LocalLow"));
  }
  foreach(var r in seen)yield return r;
 }
 static bool IsBinary(string path)=>Binaries.Contains(Path.GetExtension(path));
 static bool IsOldEnoughTemp(string path){
  try{
   var fi=new FileInfo(path);
   if(!fi.Exists)return false;
   var now=DateTime.Now;
   return (now-fi.LastWriteTime).TotalDays>7 && (now-fi.LastAccessTime).TotalDays>7;
  }catch{return false;}
 }
 static bool IsInUse(string path){
  try{
   var attrs=File.GetAttributes(path);
   var access=(attrs&FileAttributes.ReadOnly)!=0?FileAccess.Read:FileAccess.ReadWrite;
   using var fs=new FileStream(path,FileMode.Open,access,FileShare.None);
   return false;
  }catch{return true;}
 }
 static bool IsTooBroadRoot(string dir){
  try{
   if(SameDir(dir,Path.GetPathRoot(dir)??""))return true;
   var profile=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
   if(!string.IsNullOrWhiteSpace(profile)&&SameDir(dir,profile))return true;
   if(!string.IsNullOrWhiteSpace(profile)){
    var users=Directory.GetParent(profile)?.FullName;
    if(!string.IsNullOrWhiteSpace(users)&&SameDir(dir,users))return true;
   }
   if(TempRoots().Any(t=>SameDir(dir,t)))return true;
   return false;
  }catch{return true;}
 }
 static string? ExistingDirFromRegistryValue(string? raw){
  if(string.IsNullOrWhiteSpace(raw))return null;
  try{
   var s=raw.Trim();
   if(s.StartsWith('"')){var end=s.IndexOf('"',1);if(end>1)s=s[1..end];}
   s=s.Trim().Trim('"');
   var comma=s.LastIndexOf(',');
   if(comma>1 && int.TryParse(s[(comma+1)..].Trim(),out _))s=s[..comma];
   s=Environment.ExpandEnvironmentVariables(s.Trim().Trim('"'));
   var full=TryFullPath(s);
   if(full is not null){
    if(Directory.Exists(full))return full;
    if(File.Exists(full)){var d=Path.GetDirectoryName(full);if(!string.IsNullOrWhiteSpace(d)&&Directory.Exists(d))return Path.GetFullPath(d);}
   }
   var exe=s.IndexOf(".exe",StringComparison.OrdinalIgnoreCase);
   if(exe>=0){
    var cand=Environment.ExpandEnvironmentVariables(s[..(exe+4)].Trim().Trim('"'));
    var cf=TryFullPath(cand);
    if(cf is not null&&File.Exists(cf)){var d=Path.GetDirectoryName(cf);if(!string.IsNullOrWhiteSpace(d)&&Directory.Exists(d))return Path.GetFullPath(d);}
   }
  }catch{}
  return null;
 }
 static void TryAddApp(List<InstalledApp> apps,HashSet<string> seen,string? name,string? dir){
  try{
   if(string.IsNullOrWhiteSpace(dir)||!Directory.Exists(dir))return;
   var full=Path.GetFullPath(dir);
   if(IsTooBroadRoot(full))return;
   if(!seen.Add(NormalizeDir(full)))return;
   apps.Add(new(string.IsNullOrWhiteSpace(name)?"Installed application":name,full));
  }catch{}
 }
 static List<InstalledApp> InstalledApps(bool refresh=false){
  if(!refresh&&cachedApps is not null&&(DateTime.UtcNow-cachedAppsAt).TotalSeconds<60)return cachedApps;
  var apps=new List<InstalledApp>();
  var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  foreach(var hive in new[]{Registry.LocalMachine,Registry.CurrentUser})
   foreach(var keyPath in new[]{@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"})
    try{
     using var k=hive.OpenSubKey(keyPath);
     if(k is null)continue;
     foreach(var n in k.GetSubKeyNames())
      try{
       using var a=k.OpenSubKey(n);
       if(a is null)continue;
       var name=a.GetValue("DisplayName")?.ToString();
       TryAddApp(apps,seen,name,a.GetValue("InstallLocation")?.ToString());
       TryAddApp(apps,seen,name,ExistingDirFromRegistryValue(a.GetValue("DisplayIcon")?.ToString()));
       TryAddApp(apps,seen,name,ExistingDirFromRegistryValue(a.GetValue("UninstallString")?.ToString()));
      }catch{}
    }catch{}
  var list=apps.OrderByDescending(x=>x.Root.Length).ToList();
  cachedApps=list;cachedAppsAt=DateTime.UtcNow;return list;
 }
 static (string? owner,string ownership) Ownership(string path,List<InstalledApp> apps){foreach(var a in apps)if(Under(path,a.Root))return(a.Name,"Installed application");var pf=Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);var pf86=Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);if(Under(path,pf)||Under(path,pf86))return(null,"Application area — owner not confirmed");if(Under(path,Environment.GetFolderPath(Environment.SpecialFolder.Windows)))return("Windows","Windows system");return(null,"User/other file");}

 static (FileSafety,string) Safety(string path,bool cloud,List<InstalledApp> apps){
  var p=TryFullPath(path);
  if(p is null)return(FileSafety.Protected,"Unreadable path — protected");
  var name=Path.GetFileName(p);
  if(new[]{"pagefile.sys","hiberfil.sys","swapfile.sys"}.Any(n=>name.Equals(n,StringComparison.OrdinalIgnoreCase))||
     p.Contains("\\System Volume Information\\",StringComparison.OrdinalIgnoreCase)||
     p.Contains("\\Recovery\\",StringComparison.OrdinalIgnoreCase))
   return(FileSafety.Protected,"Windows/application file");
  foreach(var a in apps)if(Under(p,a.Root))return(FileSafety.Protected,$"Installed application ({a.Name}) — protected");
  if(IsInPureTemp(p)){
   if(cloud)return(FileSafety.Review,"Cloud-only — content not read or downloaded");
   if(IsInUse(p))return(FileSafety.Review,"Temporary file is in use — not marked safe to clean");
   if(!IsOldEnoughTemp(p))return(FileSafety.Review,"Temporary file is newer than 7 days — not marked safe to clean");
   return(FileSafety.SafeToClean,"Old Windows temporary file (safe candidate)");
  }
  if(AppDataRoots().Any(r=>Under(p,r)))return(FileSafety.Protected,"AppData — protected");
  var win=Environment.GetFolderPath(Environment.SpecialFolder.Windows);
  var pf=Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
  var pf86=Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
  var programData=Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
  if(Under(p,win)||Under(p,pf)||Under(p,pf86)||Under(p,programData))
   return(FileSafety.Protected,"Windows/application file");
  if(IsBinary(p))return(FileSafety.Protected,"Binary/executable file — protected");
  if(cloud)return(FileSafety.Review,"Cloud-only — content not read or downloaded");
  return(FileSafety.Review,"User review required — not marked safe for auto-clean");
 }

 static bool Cloud(FileAttributes a)=>a.HasFlag(FileAttributes.Offline)||((int)a&0x00040000)!=0||((int)a&0x00400000)!=0;
 public static async Task<StorageScanResult> ScanAsync(string root,IProgress<ScanProgress>? progress=null,CancellationToken ct=default,HashSet<string>? categories=null,long minBytes=0,long maxBytes=long.MaxValue,ManualResetEventSlim? pause=null)=>await Task.Run(()=>{
  var apps=InstalledApps(true);var list=new List<FileItem>();var pending=new Stack<string>();pending.Push(root);long files=0,bytes=0,seenBytes=0,folders=0,skipped=0;long usedBytes=1;try{var d=new DriveInfo(Path.GetPathRoot(root)!);usedBytes=Math.Max(1,d.TotalSize-d.AvailableFreeSpace);}catch{}
  while(pending.Count>0){ct.ThrowIfCancellationRequested();pause?.Wait(ct);var dir=pending.Pop();folders++;try{
   foreach(var d in Directory.EnumerateDirectories(dir)){ct.ThrowIfCancellationRequested();try{if(!File.GetAttributes(d).HasFlag(FileAttributes.ReparsePoint))pending.Push(d);else skipped++;}catch{skipped++;}}
   foreach(var file in Directory.EnumerateFiles(dir)){ct.ThrowIfCancellationRequested();pause?.Wait(ct);try{var i=new FileInfo(file);seenBytes+=Math.Max(0,i.Length);var attrs=i.Attributes;var cloud=Cloud(attrs);var cat=Category(i.Extension);if(i.Length<minBytes||i.Length>maxBytes||(categories is not null&&categories.Count>0&&!categories.Contains(cat)))continue;var s=Safety(file,cloud,apps);var own=Ownership(file,apps);list.Add(new(file,i.Length,cat,s.Item1,s.Item2,i.CreationTime,i.LastWriteTime,cloud,null,false,own.owner,own.ownership));files++;bytes+=i.Length;}catch{skipped++;}}
   progress?.Report(new(files,bytes,folders,skipped,"Scanning files",dir,Math.Min(60,seenBytes*60d/usedBytes)));
  }catch(UnauthorizedAccessException){skipped++;}catch(IOException){skipped++;}}
  MarkDuplicates(list,ct,progress,ref skipped,pause);MarkSimilarPhotos(list,ct,progress,ref skipped,pause);progress?.Report(new(files,bytes,folders,skipped,"Complete",root,100));return new StorageScanResult(root,list,list.Sum(x=>x.Bytes),skipped);},ct);
 static void MarkDuplicates(List<FileItem> files,CancellationToken ct,IProgress<ScanProgress>? progress,ref long skipped,ManualResetEventSlim? pause){
  var matchedFiles=files.Count;var matchedBytes=files.Sum(x=>x.Bytes);
  var candidates=files.Select((f,i)=>(f,i)).Where(x=>x.f.Bytes>0&&!x.f.CloudOnly&&x.f.Safety!=FileSafety.Protected).GroupBy(x=>x.f.Bytes).Where(g=>g.Count()>1).ToList();long done=0,total=candidates.Sum(g=>g.Count());
  foreach(var group in candidates){var pre=new Dictionary<string,List<(FileItem f,int i)>>(StringComparer.OrdinalIgnoreCase);
   foreach(var x in group){ct.ThrowIfCancellationRequested();pause?.Wait(ct);try{var h=HashPrefix(x.f.Path,64*1024,ct);if(!pre.TryGetValue(h,out var l))pre[h]=l=new();l.Add(x);}catch{skipped++;}done++;progress?.Report(new(matchedFiles,matchedBytes,0,skipped,"Checking duplicates",x.f.Path,60+(total==0?10:done*10d/total)));}
   foreach(var pg in pre.Values.Where(v=>v.Count>1)){var full=new Dictionary<string,List<int>>(StringComparer.OrdinalIgnoreCase);foreach(var x in pg){ct.ThrowIfCancellationRequested();pause?.Wait(ct);try{var h=HashFile(x.f.Path,ct);if(!full.TryGetValue(h,out var l))full[h]=l=new();l.Add(x.i);}catch{skipped++;}}foreach(var pair in full.Where(x=>x.Value.Count>1)){var id=pair.Key[..12];var keeper=pair.Value.Select(idx=>(idx,item:files[idx])).OrderByDescending(x=>KeeperScore(x.item)).ThenBy(x=>x.item.ModifiedAt).First().idx;foreach(var idx in pair.Value)files[idx]=files[idx] with{DuplicateGroup=id,DuplicateKeeper=idx==keeper};}}
  }}

 static ulong PerceptualHash(string path){using var fs=File.Open(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.CreateOptions=BitmapCreateOptions.PreservePixelFormat;image.DecodePixelWidth=9;image.StreamSource=fs;image.EndInit();image.Freeze();BitmapSource source=image;if(image.PixelWidth!=9||image.PixelHeight!=8){var scaled=new TransformedBitmap(image,new ScaleTransform(9d/image.PixelWidth,8d/image.PixelHeight));scaled.Freeze();source=scaled;}var gray=new FormatConvertedBitmap(source,PixelFormats.Gray8,null,0);gray.Freeze();var pixels=new byte[72];gray.CopyPixels(pixels,9,0);ulong hash=0;int bit=0;for(int y=0;y<8;y++)for(int x=0;x<8;x++,bit++)if(pixels[y*9+x]>pixels[y*9+x+1])hash|=1UL<<bit;return hash;}
 static int HammingDistance(ulong a,ulong b){var x=a^b;int distance=0;while(x!=0){distance++;x&=x-1;}return distance;} static int Similarity(ulong a,ulong b)=> (int)Math.Round((64-HammingDistance(a,b))*100d/64);
 static void MarkSimilarPhotos(List<FileItem> files,CancellationToken ct,IProgress<ScanProgress>? progress,ref long skipped,ManualResetEventSlim? pause){var candidates=files.Select((f,i)=>(f,i)).Where(x=>x.f.Category=="Images"&&!x.f.CloudOnly&&x.f.Safety!=FileSafety.Protected&&x.f.DuplicateGroup==null).ToList();var hashes=new List<(int index,ulong hash)>();int done=0;var totalBytes=files.Sum(f=>f.Bytes);foreach(var x in candidates){ct.ThrowIfCancellationRequested();pause?.Wait(ct);try{hashes.Add((x.i,PerceptualHash(x.f.Path)));}catch{skipped++;}done++;if(done%25==0)progress?.Report(new(files.Count,totalBytes,0,skipped,"Finding similar photos",x.f.Path,70+(candidates.Count==0?0:done*15d/candidates.Count)));}var buckets=new Dictionary<byte,List<int>>();for(int i=0;i<hashes.Count;i++){var key=(byte)(hashes[i].hash>>56);if(!buckets.TryGetValue(key,out var list))buckets[key]=list=new();list.Add(i);}var assigned=new HashSet<int>();int groupNo=0;for(int a=0;a<hashes.Count;a++){ct.ThrowIfCancellationRequested();pause?.Wait(ct);if(assigned.Contains(hashes[a].index))continue;var members=new List<(int index,int similarity)>{(hashes[a].index,100)};var key=(byte)(hashes[a].hash>>56);var candidateIndexes=new HashSet<int>>();for(int k=0;k<256;k++){if(HammingDistance(key,(byte)k)>6||!buckets.TryGetValue((byte)k,out var bucket))continue;foreach(var bi in bucket)if(bi>a)candidateIndexes.Add(bi);}foreach(var bi in candidateIndexes){if(assigned.Contains(hashes[bi].index))continue;var distance=HammingDistance(hashes[a].hash,hashes[bi].hash);if(distance<=6)members.Add((hashes[bi].index,Similarity(hashes[a].hash,hashes[bi].hash)));}if(members.Count>=2){groupNo++;var id=$"SIM-{groupNo:0000}";foreach(var x in members){files[x.index]=files[x.index] with{SimilarGroup=id,Similarity=x.similarity};assigned.Add(x.index);}}if(a%50==0)progress?.Report(new(files.Count,totalBytes,0,skipped,"Grouping visually similar photos",files[hashes[a].index].Path,85+(hashes.Count==0?0:a*14d/hashes.Count)));}}
 static string HashPrefix(string path,int limit,CancellationToken ct){using var fs=File.Open(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var h=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var b=new byte[64*1024];int left=limit,n;while(left>0&&(n=fs.Read(b,0,Math.Min(b.Length,left)))>0){ct.ThrowIfCancellationRequested();h.AppendData(b,0,n);left-=n;}return Convert.ToHexString(h.GetHashAndReset());}
 static string HashFile(string path,CancellationToken ct){using var fs=File.Open(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var h=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var b=new byte[1024*1024];int n;while((n=fs.Read(b,0,b.Length))>0){ct.ThrowIfCancellationRequested();h.AppendData(b,0,n);}return Convert.ToHexString(h.GetHashAndReset());}
 public static bool CanDelete(FileItem item){
  if(item is null||item.CloudOnly||item.DuplicateKeeper||item.Safety!=FileSafety.SafeToClean)return false;
  try{return Safety(item.Path,item.CloudOnly,InstalledApps()).Item1==FileSafety.SafeToClean;}catch{return false;}
 }
 public static void DeleteToRecycleBin(FileItem item){if(!CanDelete(item))throw new InvalidOperationException("SmartRAM blocked deletion — only old pure temporary files are allowed.");Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(item.Path,Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);}
}
