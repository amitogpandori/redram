using System.IO;
using System.Runtime.InteropServices;
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
/// <summary>Outcome of one Recycle Bin pass. Every path is listed in exactly one of the lists.</summary>
public record RecycleOutcome(IReadOnlyList<FileItem> Moved,IReadOnlyList<(FileItem Item,string Reason)> Skipped,bool Cancelled){
 public long MovedBytes=>Moved.Sum(x=>x.Bytes);
}
public static class StorageScanner{
 record InstalledApp(string Name,string Root,string NormRoot);
 /// <summary>Folders resolved once per scan instead of once per file (big speed-up on drives with millions of files).</summary>
 sealed record PathContext(string[] TempRoots,string[] AppDataRoots,string Windows,string ProgramFiles,string ProgramFilesX86,string ProgramData,string Downloads);
 static List<InstalledApp>? cachedApps;
 static DateTime cachedAppsAt;
 static PathContext? ctxCache;
 static readonly object Sync=new();
 static readonly HashSet<string> Video=new(StringComparer.OrdinalIgnoreCase){".mp4",".mkv",".avi",".mov",".wmv",".webm",".m4v"};
 static readonly HashSet<string> Images=new(StringComparer.OrdinalIgnoreCase){".jpg",".jpeg",".png",".gif",".webp",".bmp",".heic",".tiff"};
 static readonly HashSet<string> Audio=new(StringComparer.OrdinalIgnoreCase){".mp3",".wav",".flac",".aac",".m4a",".ogg",".wma"};
 static readonly HashSet<string> Docs=new(StringComparer.OrdinalIgnoreCase){".pdf",".doc",".docx",".xls",".xlsx",".ppt",".pptx",".txt",".csv",".rtf"};
 static readonly HashSet<string> Archives=new(StringComparer.OrdinalIgnoreCase){".zip",".rar",".7z",".tar",".gz",".iso"};
 static readonly HashSet<string> Binaries=new(StringComparer.OrdinalIgnoreCase){
  ".exe",".dll",".sys",".msi",".msix",".msixbundle",".appx",".appxbundle",".ocx",".drv",".cpl",".scr",".com",
  ".bat",".cmd",".ps1",".jar",".node",".efi",".ax",".pyd",".vbs",".vbe",".js",".jse",".wsf",".wsh",".hta",".pif",".msp",".mst",".msc"
 };
 static readonly string[] SystemFileNames={"pagefile.sys","hiberfil.sys","swapfile.sys"};
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
 /// <summary>Both arguments already normalized with <see cref="NormalizeDir"/>.</summary>
 static bool UnderNorm(string normPath,string normRoot)=>normRoot.Length>1&&normPath.StartsWith(normRoot,StringComparison.OrdinalIgnoreCase);
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
 static string NormOrEmpty(string? p)=>string.IsNullOrWhiteSpace(p)?"":NormalizeDir(p);
 static PathContext BuildContext(){
  var profile=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
  return new PathContext(TempRoots().ToArray(),AppDataRoots().ToArray(),
   NormOrEmpty(Environment.GetFolderPath(Environment.SpecialFolder.Windows)),
   NormOrEmpty(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),
   NormOrEmpty(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)),
   NormOrEmpty(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)),
   string.IsNullOrWhiteSpace(profile)?"":NormalizeDir(Path.Combine(profile,"Downloads")));
 }
 static PathContext Ctx(bool refresh=false){lock(Sync){if(refresh||ctxCache is null)ctxCache=BuildContext();return ctxCache;}}
 static bool IsInPureTemp(string normPath)=>Ctx().TempRoots.Any(t=>UnderNorm(normPath,t));
 /// <summary>True for files inside the current user's Downloads folder.</summary>
 public static bool IsInDownloads(string path){var d=Ctx().Downloads;return d.Length>0&&UnderNorm(NormalizeDir(path),d);}
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
   var norm=NormalizeDir(full);
   if(!seen.Add(norm))return;
   apps.Add(new(string.IsNullOrWhiteSpace(name)?"Installed application":name,full,norm));
  }catch{}
 }
 static List<InstalledApp> InstalledApps(bool refresh=false){
  lock(Sync){if(!refresh&&cachedApps is not null&&(DateTime.UtcNow-cachedAppsAt).TotalSeconds<60)return cachedApps;}
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
  lock(Sync){cachedApps=list;cachedAppsAt=DateTime.UtcNow;}
  return list;
 }
 static (string? owner,string ownership) Ownership(string normPath,List<InstalledApp> apps){
  foreach(var a in apps)if(UnderNorm(normPath,a.NormRoot))return(a.Name,"Installed application");
  var c=Ctx();
  if(UnderNorm(normPath,c.ProgramFiles)||UnderNorm(normPath,c.ProgramFilesX86))return(null,"Application area — owner not confirmed");
  if(UnderNorm(normPath,c.Windows))return("Windows","Windows system");
  return(null,"User/other file");
 }

 /// <summary>
 /// Safety classification. Protected = never deletable by RED RAM (Windows, installed apps, Program Files, ProgramData, AppData, binaries/scripts, system files).
 /// SafeToClean = old, unused files in a pure Windows temp folder (the only files "Clean safe junk" may touch).
 /// Review = the user's own files: shown, never auto-cleaned, and only moved to the Recycle Bin after the user ticks them and confirms.
 /// </summary>
 static (FileSafety,string) Safety(string path,bool cloud,List<InstalledApp> apps,FileAttributes? knownAttributes=null){
  var p=TryFullPath(path);
  if(p is null)return(FileSafety.Protected,"Unreadable path — protected");
  var np=NormalizeDir(p);
  var name=Path.GetFileName(p);
  FileAttributes attrs;
  try{attrs=knownAttributes??File.GetAttributes(p);}catch{return(FileSafety.Protected,"Attributes unreadable — protected");}
  if((attrs&FileAttributes.System)!=0)return(FileSafety.Protected,"Windows system file — protected");
  var c=Ctx();
  var dir=Path.GetDirectoryName(p);
  if(c.Windows.Length>0&&dir is not null&&string.Equals(NormalizeDir(dir),NormalizeDir(Path.GetPathRoot(c.Windows)??""),StringComparison.OrdinalIgnoreCase))
   return(FileSafety.Protected,"System drive root file — protected");
  if(SystemFileNames.Any(n=>name.Equals(n,StringComparison.OrdinalIgnoreCase))||
     p.Contains("\\System Volume Information\\",StringComparison.OrdinalIgnoreCase)||
     p.Contains("\\Recovery\\",StringComparison.OrdinalIgnoreCase)||
     p.Contains("\\$Recycle.Bin\\",StringComparison.OrdinalIgnoreCase))
   return(FileSafety.Protected,"Windows/application file");
  foreach(var a in apps)if(UnderNorm(np,a.NormRoot))return(FileSafety.Protected,$"Installed application ({a.Name}) — protected");
  if(IsInPureTemp(np)){
   if(cloud)return(FileSafety.Review,"Cloud-only — content not read or downloaded");
   // Cheap age check first; the exclusive-open "in use" probe only runs for files old enough to qualify.
   if(!IsOldEnoughTemp(p))return(FileSafety.Review,"Temporary file is newer than 7 days — not marked safe to clean");
   if(IsInUse(p))return(FileSafety.Review,"Temporary file is in use — not marked safe to clean");
   return(FileSafety.SafeToClean,"Old Windows temporary file (safe candidate)");
  }
  if(c.AppDataRoots.Any(r=>UnderNorm(np,r)))return(FileSafety.Protected,"AppData — protected");
  if(UnderNorm(np,c.Windows)||UnderNorm(np,c.ProgramFiles)||UnderNorm(np,c.ProgramFilesX86)||UnderNorm(np,c.ProgramData))
   return(FileSafety.Protected,"Windows/application file");
  if(IsBinary(p))return(FileSafety.Protected,"Binary/executable file — protected");
  if(cloud)return(FileSafety.Review,"Cloud-only — content not read or downloaded");
  return(FileSafety.Review,"Your file — review before removing");
 }

 static bool Cloud(FileAttributes a)=>a.HasFlag(FileAttributes.Offline)||((int)a&0x00040000)!=0||((int)a&0x00400000)!=0;
 static readonly EnumerationOptions EnumOptions=new(){RecurseSubdirectories=false,IgnoreInaccessible=false,AttributesToSkip=0,ReturnSpecialDirectories=false};

 public static async Task<StorageScanResult> ScanAsync(string root,IProgress<ScanProgress>? progress=null,CancellationToken ct=default,HashSet<string>? categories=null,long minBytes=0,long maxBytes=long.MaxValue,ManualResetEventSlim? pause=null,bool findSimilarPhotos=true)=>await Task.Run(()=>{
  Ctx(true);
  var apps=InstalledApps(true);var list=new List<FileItem>();var pending=new Stack<string>();pending.Push(root);long files=0,bytes=0,seenBytes=0,folders=0,skipped=0;long usedBytes=1;
  try{var d=new DriveInfo(Path.GetPathRoot(root)!);usedBytes=Math.Max(1,d.TotalSize-d.AvailableFreeSpace);}catch{}
  var clock=System.Diagnostics.Stopwatch.StartNew();long lastReport=-1000;
  void Report(string phase,string current,double pct,bool force=false){if(progress is null)return;var now=clock.ElapsedMilliseconds;if(!force&&now-lastReport<120)return;lastReport=now;progress.Report(new(files,bytes,folders,skipped,phase,current,pct));}
  while(pending.Count>0){
   ct.ThrowIfCancellationRequested();pause?.Wait(ct);
   var dir=pending.Pop();folders++;
   List<FileSystemInfo> entries;
   try{entries=new DirectoryInfo(dir).EnumerateFileSystemInfos("*",EnumOptions).ToList();}
   catch(UnauthorizedAccessException){skipped++;continue;}catch(IOException){skipped++;continue;}catch(System.Security.SecurityException){skipped++;continue;}
   foreach(var e in entries){
    ct.ThrowIfCancellationRequested();
    try{
     if(e is DirectoryInfo di){
      // Never follow junctions/symlinks: avoids loops and double counting.
      if((di.Attributes&FileAttributes.ReparsePoint)!=0)skipped++;else pending.Push(di.FullName);
      continue;
     }
     if(e is not FileInfo fi)continue;
     var len=Math.Max(0,fi.Length);seenBytes+=len;
     var cat=Category(fi.Extension);
     if(len<minBytes||len>maxBytes||(categories is not null&&categories.Count>0&&!categories.Contains(cat)))continue;
     pause?.Wait(ct);
     var cloud=Cloud(fi.Attributes);
     var full=fi.FullName;
     var s=Safety(full,cloud,apps,fi.Attributes);var own=Ownership(NormalizeDir(full),apps);
     list.Add(new(full,len,cat,s.Item1,s.Item2,fi.CreationTime,fi.LastWriteTime,cloud,null,false,own.owner,own.ownership));
     files++;bytes+=len;
    }catch(OperationCanceledException){throw;}catch{skipped++;}
   }
   Report("Scanning files",dir,Math.Min(60,seenBytes*60d/usedBytes));
  }
  Report("Checking duplicates","",60,true);
  MarkDuplicates(list,ct,progress,ref skipped,pause);
  if(findSimilarPhotos)MarkSimilarPhotos(list,ct,progress,ref skipped,pause);
  progress?.Report(new(files,bytes,folders,skipped,"Complete",root,100));
  return new StorageScanResult(root,list,list.Sum(x=>x.Bytes),skipped);
 },ct);

 /// <summary>Files smaller than this are not hashed for duplicates (they free almost no space and slow scans down).</summary>
 const long MinDuplicateBytes=16*1024;
 static void MarkDuplicates(List<FileItem> files,CancellationToken ct,IProgress<ScanProgress>? progress,ref long skipped,ManualResetEventSlim? pause){
  var matchedFiles=files.Count;var matchedBytes=files.Sum(x=>x.Bytes);
  var candidates=files.Select((f,i)=>(f,i)).Where(x=>x.f.Bytes>=MinDuplicateBytes&&!x.f.CloudOnly&&x.f.Safety!=FileSafety.Protected).GroupBy(x=>x.f.Bytes).Where(g=>g.Count()>1).ToList();long done=0,total=candidates.Sum(g=>g.Count());
  var clock=System.Diagnostics.Stopwatch.StartNew();long last=-1000;
  foreach(var group in candidates){var pre=new Dictionary<string,List<(FileItem f,int i)>>(StringComparer.OrdinalIgnoreCase);
   foreach(var x in group){ct.ThrowIfCancellationRequested();pause?.Wait(ct);try{var h=HashPrefix(x.f.Path,64*1024,ct);if(!pre.TryGetValue(h,out var l))pre[h]=l=new();l.Add(x);}catch(OperationCanceledException){throw;}catch{skipped++;}done++;
    if(clock.ElapsedMilliseconds-last>=120){last=clock.ElapsedMilliseconds;progress?.Report(new(matchedFiles,matchedBytes,0,skipped,"Checking duplicates",x.f.Path,60+(total==0?10:done*10d/total)));}}
   foreach(var pg in pre.Values.Where(v=>v.Count>1)){var full=new Dictionary<string,List<int>>(StringComparer.OrdinalIgnoreCase);foreach(var x in pg){ct.ThrowIfCancellationRequested();pause?.Wait(ct);try{var h=HashFile(x.f.Path,ct);if(!full.TryGetValue(h,out var l))full[h]=l=new();l.Add(x.i);}catch(OperationCanceledException){throw;}catch{skipped++;}}foreach(var pair in full.Where(x=>x.Value.Count>1)){var id=pair.Key[..12];var keeper=pair.Value.Select(idx=>(idx,item:files[idx])).OrderByDescending(x=>KeeperScore(x.item)).ThenBy(x=>x.item.ModifiedAt).First().idx;foreach(var idx in pair.Value)files[idx]=files[idx] with{DuplicateGroup=id,DuplicateKeeper=idx==keeper};}}
  }}

 static ulong PerceptualHash(string path){using var fs=File.Open(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.CreateOptions=BitmapCreateOptions.PreservePixelFormat;image.DecodePixelWidth=9;image.StreamSource=fs;image.EndInit();image.Freeze();BitmapSource source=image;if(image.PixelWidth!=9||image.PixelHeight!=8){var scaled=new TransformedBitmap(image,new ScaleTransform(9d/image.PixelWidth,8d/image.PixelHeight));scaled.Freeze();source=scaled;}var gray=new FormatConvertedBitmap(source,PixelFormats.Gray8,null,0);gray.Freeze();var pixels=new byte[72];gray.CopyPixels(pixels,9,0);ulong hash=0;int bit=0;for(int y=0;y<8;y++)for(int x=0;x<8;x++,bit++)if(pixels[y*9+x]>pixels[y*9+x+1])hash|=1UL<<bit;return hash;}
 static int HammingDistance(ulong a,ulong b){var x=a^b;int distance=0;while(x!=0){distance++;x&=x-1;}return distance;} static int Similarity(ulong a,ulong b)=> (int)Math.Round((64-HammingDistance(a,b))*100d/64);
 static void MarkSimilarPhotos(List<FileItem> files,CancellationToken ct,IProgress<ScanProgress>? progress,ref long skipped,ManualResetEventSlim? pause){var candidates=files.Select((f,i)=>(f,i)).Where(x=>x.f.Category=="Images"&&!x.f.CloudOnly&&x.f.Safety!=FileSafety.Protected&&x.f.DuplicateGroup==null).ToList();var hashes=new List<(int index,ulong hash)>();int done=0;var totalBytes=files.Sum(f=>f.Bytes);foreach(var x in candidates){ct.ThrowIfCancellationRequested();pause?.Wait(ct);try{hashes.Add((x.i,PerceptualHash(x.f.Path)));}catch{skipped++;}done++;if(done%25==0)progress?.Report(new(files.Count,totalBytes,0,skipped,"Finding similar photos",x.f.Path,70+(candidates.Count==0?0:done*15d/candidates.Count)));}var buckets=new Dictionary<byte,List<int>>();for(int i=0;i<hashes.Count;i++){var key=(byte)(hashes[i].hash>>56);if(!buckets.TryGetValue(key,out var list))buckets[key]=list=new();list.Add(i);}var assigned=new HashSet<int>();int groupNo=0;for(int a=0;a<hashes.Count;a++){ct.ThrowIfCancellationRequested();pause?.Wait(ct);if(assigned.Contains(hashes[a].index))continue;var members=new List<(int index,int similarity)>{(hashes[a].index,100)};var key=(byte)(hashes[a].hash>>56);var candidateIndexes=new HashSet<int>();for(int k=0;k<256;k++){if(HammingDistance(key,(byte)k)>6||!buckets.TryGetValue((byte)k,out var bucket))continue;foreach(var bi in bucket)if(bi>a)candidateIndexes.Add(bi);}foreach(var bi in candidateIndexes){if(assigned.Contains(hashes[bi].index))continue;var distance=HammingDistance(hashes[a].hash,hashes[bi].hash);if(distance<=6)members.Add((hashes[bi].index,Similarity(hashes[a].hash,hashes[bi].hash)));}if(members.Count>=2){groupNo++;var id=$"SIM-{groupNo:0000}";foreach(var x in members){files[x.index]=files[x.index] with{SimilarGroup=id,Similarity=x.similarity};assigned.Add(x.index);}}if(a%50==0)progress?.Report(new(files.Count,totalBytes,0,skipped,"Grouping visually similar photos",files[hashes[a].index].Path,85+(hashes.Count==0?0:a*14d/hashes.Count)));}}
 static string HashPrefix(string path,int limit,CancellationToken ct){using var fs=File.Open(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var h=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var b=new byte[64*1024];int left=limit,n;while(left>0&&(n=fs.Read(b,0,Math.Min(b.Length,left)))>0){ct.ThrowIfCancellationRequested();h.AppendData(b,0,n);left-=n;}return Convert.ToHexString(h.GetHashAndReset());}
 static string HashFile(string path,CancellationToken ct){using var fs=File.Open(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var h=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var b=new byte[1024*1024];int n;while((n=fs.Read(b,0,b.Length))>0){ct.ThrowIfCancellationRequested();h.AppendData(b,0,n);}return Convert.ToHexString(h.GetHashAndReset());}
 /// <summary>
 /// Auto-clean rule (unchanged): only old, unused files in a pure Windows temp folder. Used by "Clean safe junk".
 /// </summary>
 public static bool CanDelete(FileItem item){
  if(item is null||item.CloudOnly||item.DuplicateKeeper||item.Safety!=FileSafety.SafeToClean)return false;
  try{return Safety(item.Path,item.CloudOnly,InstalledApps()).Item1==FileSafety.SafeToClean;}catch{return false;}
 }
 /// <summary>Whether the user may tick this file in the list. Protected and cloud-only files can never be selected.</summary>
 public static bool IsSelectable(FileItem item)=>item is not null&&!item.CloudOnly&&item.Safety!=FileSafety.Protected;
 /// <summary>
 /// Manual (user-ticked) rule: the file must still classify as non-Protected right now, must not be a cloud placeholder,
 /// must be unchanged since the scan, and must be on a fixed drive (so it really goes to the Recycle Bin, never permanently deleted).
 /// </summary>
 public static bool CanRecycle(FileItem item,out string reason){
  reason="";
  if(item is null){reason="Unknown file";return false;}
  if(item.CloudOnly){reason="Cloud-only file — RED RAM never deletes cloud placeholders";return false;}
  if(item.Safety==FileSafety.Protected){reason=item.Reason;return false;}
  try{
   var fi=new FileInfo(item.Path);
   if(!fi.Exists){reason="File no longer exists";return false;}
   if(fi.Length!=item.Bytes){reason="File changed since the scan — rescan first";return false;}
   if(Cloud(fi.Attributes)){reason="Cloud-only file — RED RAM never deletes cloud placeholders";return false;}
   var s=Safety(item.Path,false,InstalledApps());
   if(s.Item1==FileSafety.Protected){reason=s.Item2;return false;}
   var root=Path.GetPathRoot(fi.FullName);
   if(string.IsNullOrEmpty(root)||root.StartsWith(@"\\")){reason="Network location — no Recycle Bin, RED RAM will not delete it";return false;}
   if(new DriveInfo(root).DriveType!=DriveType.Fixed){reason="This drive has no Recycle Bin — RED RAM will not delete permanently";return false;}
   return true;
  }catch(Exception ex){reason=ex.Message;return false;}
 }
 /// <summary>
 /// Never removes every copy of a duplicate group: if the selection contains all remaining copies of a group,
 /// RED RAM's recommended keeper (or the newest copy) is taken out of the selection and reported in <paramref name="keptBack"/>.
 /// </summary>
 public static List<FileItem> KeepOneCopyPerDuplicateGroup(IEnumerable<FileItem> selection,IEnumerable<FileItem> allFiles,out List<FileItem> keptBack){
  var sel=selection.Distinct().ToList();keptBack=new();
  var selectedSet=new HashSet<string>(sel.Select(x=>x.Path),StringComparer.OrdinalIgnoreCase);
  var all=allFiles as IReadOnlyCollection<FileItem>??allFiles.ToList();
  foreach(var g in sel.Where(x=>x.DuplicateGroup!=null).GroupBy(x=>x.DuplicateGroup!).ToList()){
   var groupAll=all.Where(x=>x.DuplicateGroup==g.Key).ToList();
   if(groupAll.All(x=>selectedSet.Contains(x.Path))){
    var keep=groupAll.FirstOrDefault(x=>x.DuplicateKeeper)??groupAll.OrderByDescending(x=>x.ModifiedAt).First();
    sel.RemoveAll(x=>string.Equals(x.Path,keep.Path,StringComparison.OrdinalIgnoreCase));keptBack.Add(keep);
   }
  }
  return sel;
 }
 /// <summary>Single-file compatibility wrapper. Throws when RED RAM's rules block the file.</summary>
 public static void DeleteToRecycleBin(FileItem item){
  if(!CanRecycle(item,out var why))throw new InvalidOperationException("RED RAM blocked deletion — "+why);
  var err=ShellRecycle(item.Path);
  if(err!=null)throw new IOException(err);
 }
 /// <summary>
 /// Moves files to the Recycle Bin on a background STA thread, re-checking every file's safety right before it moves.
 /// Nothing is deleted permanently: FOF_ALLOWUNDO + fixed-drive check + FOF_WANTNUKEWARNING as a last guard.
 /// </summary>
 public static Task<RecycleOutcome> RecycleAsync(IReadOnlyList<FileItem> items,IProgress<(int Done,int Total,string Current)>? progress,CancellationToken ct){
  var tcs=new TaskCompletionSource<RecycleOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
  var t=new Thread(()=>{
   var moved=new List<FileItem>();var skipped=new List<(FileItem,string)>();bool cancelled=false;
   try{
    for(int i=0;i<items.Count;i++){
     if(ct.IsCancellationRequested){cancelled=true;for(int j=i;j<items.Count;j++)skipped.Add((items[j],"Cancelled before it was moved"));break;}
     var it=items[i];progress?.Report((i,items.Count,it.Path));
     if(!CanRecycle(it,out var why)){skipped.Add((it,why));continue;}
     var err=ShellRecycle(it.Path);
     if(err is null&&!File.Exists(it.Path))moved.Add(it);else skipped.Add((it,err??"Windows could not move it — it may be open in another app"));
    }
    progress?.Report((items.Count,items.Count,""));
    tcs.TrySetResult(new RecycleOutcome(moved,skipped,cancelled));
   }catch(Exception ex){tcs.TrySetException(ex);}
  }){IsBackground=true,Name="RED RAM Recycle"};
  t.SetApartmentState(ApartmentState.STA);t.Start();
  return tcs.Task;
 }
 const uint FO_DELETE=0x0003;
 const ushort FOF_SILENT=0x0004,FOF_NOCONFIRMATION=0x0010,FOF_ALLOWUNDO=0x0040,FOF_NOERRORUI=0x0400,FOF_WANTNUKEWARNING=0x4000;
 [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
 struct SHFILEOPSTRUCT{public IntPtr hwnd;public uint wFunc;[MarshalAs(UnmanagedType.LPWStr)]public string pFrom;[MarshalAs(UnmanagedType.LPWStr)]public string? pTo;public ushort fFlags;[MarshalAs(UnmanagedType.Bool)]public bool fAnyOperationsAborted;public IntPtr hNameMappings;[MarshalAs(UnmanagedType.LPWStr)]public string? lpszProgressTitle;}
 [DllImport("shell32.dll",CharSet=CharSet.Unicode)]static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
 /// <returns>null on success, otherwise a short reason.</returns>
 static string? ShellRecycle(string path){
  var op=new SHFILEOPSTRUCT{wFunc=FO_DELETE,pFrom=Path.GetFullPath(path)+"\0\0",fFlags=(ushort)(FOF_ALLOWUNDO|FOF_NOCONFIRMATION|FOF_SILENT|FOF_NOERRORUI|FOF_WANTNUKEWARNING)};
  var rc=SHFileOperation(ref op);
  if(op.fAnyOperationsAborted)return "Cancelled — Windows did not send it to the Recycle Bin";
  return rc==0?null:$"Windows could not move it (code 0x{rc:X}) — it may be open in another app";
 }
}
