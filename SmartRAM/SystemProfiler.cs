using Microsoft.Win32;
using System.IO;
using System.Diagnostics;
using System.Management;
namespace RED RAM;
public record PcProfile(string Cpu,string Windows,double RamGb,string DiskModel,string DiskKind,double DiskGb,double FreeGb,string PageFile,double PageFileGb,int LogicalCores,double FreePercent);
public static class SystemProfiler {
 public static PcProfile Scan(){
  string cpu=Q("Win32_Processor","Name"), os=Environment.OSVersion.VersionString;
  double ram=0; try{ram=Convert.ToDouble(Q("Win32_ComputerSystem","TotalPhysicalMemory"))/1073741824d;}catch{}
  var root=Path.GetPathRoot(Environment.SystemDirectory)!; var di=new DriveInfo(root);
  string model="Unknown",kind="Unknown";
  try{using var s=new ManagementObjectSearcher("SELECT Model,MediaType,InterfaceType,PNPDeviceID FROM Win32_DiskDrive"); foreach(ManagementObject d in s.Get()){model=$"{d["Model"]}"; var all=($"{d["Model"]} {d["MediaType"]} {d["InterfaceType"]} {d["PNPDeviceID"]}").ToUpperInvariant(); kind=all.Contains("NVME")?"NVMe SSD":all.Contains("SSD")?"SSD":all.Contains("USB")?"USB storage":all.Contains("FIXED")?"Fixed disk":"Disk"; break;}}catch{}
  double pf=0; string pfText="System managed / not reported";
  try{using var s=new ManagementObjectSearcher("SELECT Name,AllocatedBaseSize FROM Win32_PageFileUsage"); var parts=new List<string>(); foreach(ManagementObject p in s.Get()){double mb=Convert.ToDouble(p["AllocatedBaseSize"]??0);pf+=mb/1024d;parts.Add($"{p["Name"]} ({mb/1024d:F1} GB)");} if(parts.Count>0)pfText=string.Join(", ",parts);}catch{}
  double disk=di.TotalSize/1073741824d, free=di.AvailableFreeSpace/1073741824d; return new(cpu,os,ram,model,kind,disk,free,pfText,pf,Environment.ProcessorCount,disk>0?free/disk*100:0);
 }
 static string Q(string cls,string prop){try{using var s=new ManagementObjectSearcher($"SELECT {prop} FROM {cls}"); foreach(ManagementObject x in s.Get())return $"{x[prop]}".Trim();}catch{} return "Unknown";}
}
public record BenchResult(double WriteMbps,double ReadMbps);
public static class StorageBenchmark {
 public static async Task<BenchResult> RunAsync(){
  string root;try{root=VirtualMemoryManager.TargetInfo().Root;}catch{root=Path.GetPathRoot(Environment.SystemDirectory)!;}var dir=Path.Combine(root,"RED RAM-Benchmark");Directory.CreateDirectory(dir);var path=Path.Combine(dir,$"smartram-bench-{Environment.ProcessId}.tmp"); const int size=64*1024*1024;var drive=new DriveInfo(root);if(!drive.IsReady||drive.AvailableFreeSpace<size*3L)throw new IOException($"Not enough free space on {root} for a safe storage benchmark."); byte[] buf=new byte[1024*1024]; new Random(42).NextBytes(buf);
  try{var sw=Stopwatch.StartNew(); await using(var fs=new FileStream(path,FileMode.Create,FileAccess.Write,FileShare.None,buf.Length,FileOptions.SequentialScan|FileOptions.WriteThrough)){for(int i=0;i<64;i++)await fs.WriteAsync(buf); await fs.FlushAsync();} sw.Stop(); double w=64/sw.Elapsed.TotalSeconds;
  sw.Restart(); await using(var fs=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,buf.Length,FileOptions.SequentialScan)){while(await fs.ReadAsync(buf)>0){}} sw.Stop(); double r=64/sw.Elapsed.TotalSeconds; return new(w,r);}finally{try{if(File.Exists(path))File.Delete(path);if(Directory.Exists(dir)&&!Directory.EnumerateFileSystemEntries(dir).Any())Directory.Delete(dir);}catch{}}
 }
}
public record Recommendation(string Title,string Detail,double SuggestedGb);
public static class RecommendationEngine {
 public static Recommendation Build(PcProfile p,BenchResult? b){
  PagefileTargetInfo? t=null;try{t=VirtualMemoryManager.TargetInfo();}catch{}
  double capacity=t?.CapacityGb??p.DiskGb,free=t?.FreeGb??p.FreeGb,current=t?.CurrentAllocatedGb??p.PageFileGb,reserve=t?.ReserveGb??Math.Max(10,capacity*.10);
  string root=t?.Root??"system drive";
  if(t is {ConfiguredEntries:>1})return new("Review multiple pagefiles","RED RAM detected multiple configured pagefiles. Monitoring remains active, but automatic virtual-memory changes are blocked to avoid replacing an advanced Windows configuration.",0);
  if(free<8)return new("RED RAM active · storage protected",$"RED RAM monitoring remains active. Free storage on the pagefile drive {root} is critically low, so RED RAM will keep the current Windows pagefile.",0);
  if(p.RamGb>=32)return new("Windows-managed memory recommended","This PC already has substantial physical RAM. RED RAM recommends monitoring first and changing virtual memory only if measured commit pressure requires it.",0);
  double safeTarget=Math.Max(0,current+free-reserve);double ideal=p.RamGb<=4?12:16;double target=Math.Min(ideal,safeTarget);
  if(b is {ReadMbps:<150})return new("Hardware upgrade preferred","Storage appears relatively slow for heavy paging. Keep Windows pagefile enabled; additional physical RAM is likely the better upgrade.",0);
  if(target<=current+0.5)return new("RED RAM active · keep current pagefile",$"Monitoring and memory intelligence remain active. RED RAM is preserving about {reserve:F0} GB on {root}, so the existing pagefile is kept.",0);
  return new("Good candidate for adaptive virtual memory",$"Based on {p.RamGb:F0} GB RAM and the actual pagefile drive {root} with {free:F0} GB currently free, RED RAM can safely target about {target:F0} GB of pagefile capacity while preserving about {reserve:F0} GB for Windows. This can improve memory headroom for demanding apps, but it does not turn storage into physical RAM.",Math.Round(target));
 }
}
