using System.Diagnostics;
using System.IO;
using System.Management;

namespace SmartRAM;

public enum RecoveryConfidence { Low, Medium, High }
public record DiskRecoveryAssessment(string Drive,string FileSystem,string VolumeLabel,long Capacity,long Free,bool Accessible,string Health,string Problem,RecoveryConfidence Confidence,string Evidence,string Recommendation,bool CanUseWindowsRepair,bool BitLockerLocked,string PhysicalDisk);
public record DiskRepairResult(bool Started,string Message);

public static class DiskRecoveryEngine {
 public static List<string> CandidateDrives(){
  var drives=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  foreach(var d in DriveInfo.GetDrives())try{if(d.DriveType is DriveType.Fixed or DriveType.Removable)drives.Add(d.Name[..2]);}catch{}
  try{using var q=new ManagementObjectSearcher("SELECT DeviceID FROM Win32_LogicalDisk WHERE DriveType=2 OR DriveType=3");foreach(ManagementObject d in q.Get()){var id=$"{d["DeviceID"]}";if(id.Length>=2)drives.Add(id[..2]);}}catch{}
  return drives.OrderBy(x=>x).ToList();
 }
 static (string disk,string health) PhysicalDiskFor(string drive,List<string> evidence){
  try{
   using var partitions=new ManagementObjectSearcher($"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{drive}'}} WHERE AssocClass=Win32_LogicalDiskToPartition");
   foreach(ManagementObject part in partitions.Get()){
    var partId=$"{part["DeviceID"]}";evidence.Add("Partition: "+partId);
    var escaped=partId.Replace("\\","\\\\").Replace("'","\\'");
    using var disks=new ManagementObjectSearcher($"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{escaped}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition");
    foreach(ManagementObject disk in disks.Get()){var model=$"{disk["Model"]}".Trim();var status=$"{disk["Status"]}".Trim();var index=$"{disk["Index"]}".Trim();var name=$"Disk {index} · {(string.IsNullOrWhiteSpace(model)?"Unknown model":model)}";evidence.Add($"Physical disk: {name}");evidence.Add($"Selected disk Windows status: {(string.IsNullOrWhiteSpace(status)?"Unknown":status)}");return(name,string.IsNullOrWhiteSpace(status)?"Unknown":status);}
   }
  }catch(Exception ex){evidence.Add("Physical-disk mapping unavailable: "+ex.Message);}
  return("Unknown","Unknown");
 }
 static bool BitLockerLocked(string drive,List<string> evidence){
  try{
   var psi=new ProcessStartInfo("manage-bde.exe",$"-status {drive}"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
   using var p=Process.Start(psi);if(p is null)return false;
   var read=p.StandardOutput.ReadToEndAsync();if(!p.WaitForExit(5000)){try{p.Kill(true);}catch{}evidence.Add("BitLocker status check timed out.");return false;}
   var o=read.GetAwaiter().GetResult();var locked=o.Contains("Lock Status:",StringComparison.OrdinalIgnoreCase)&&o.Contains("Locked",StringComparison.OrdinalIgnoreCase);
   if(o.Contains("BitLocker",StringComparison.OrdinalIgnoreCase))evidence.Add(locked?"BitLocker volume is locked.":"BitLocker status detected; volume is not reported locked.");return locked;
  }catch{return false;}
 }
 public static Task<DiskRecoveryAssessment> AnalyzeAsync(string drive,CancellationToken ct)=>Task.Run(()=>{
  ct.ThrowIfCancellationRequested();drive=drive.Trim().TrimEnd('\\');if(drive.Length==1)drive+=":";string fs="Unknown",label="";long cap=0,free=0;bool accessible=false;var evidence=new List<string>();
  try{var di=new DriveInfo(drive+"\\");if(di.IsReady){accessible=true;fs=di.DriveFormat;label=di.VolumeLabel;cap=di.TotalSize;free=di.AvailableFreeSpace;evidence.Add("Windows can mount and read the volume.");}else evidence.Add("Windows reports the volume is not ready.");}catch(Exception ex){evidence.Add("Windows cannot mount the volume: "+ex.Message);}
  try{using var q=new ManagementObjectSearcher($"SELECT FileSystem,VolumeName,Size,FreeSpace FROM Win32_LogicalDisk WHERE DeviceID='{drive}'");foreach(ManagementObject d in q.Get()){var reportedFs=$"{d["FileSystem"]}".Trim();if(!string.IsNullOrWhiteSpace(reportedFs))fs=reportedFs;label=$"{d["VolumeName"]}".Trim();long.TryParse($"{d["Size"]}",out cap);long.TryParse($"{d["FreeSpace"]}",out free);}}catch{}
  ct.ThrowIfCancellationRequested();var mapped=PhysicalDiskFor(drive,evidence);var health=mapped.health;var bitLockerLocked=BitLockerLocked(drive,evidence);ct.ThrowIfCancellationRequested();
  var raw=string.IsNullOrWhiteSpace(fs)||fs.Equals("RAW",StringComparison.OrdinalIgnoreCase)||fs.Equals("Unknown",StringComparison.OrdinalIgnoreCase);string problem;RecoveryConfidence confidence;bool repair;
  var unhealthy=!health.Equals("OK",StringComparison.OrdinalIgnoreCase)&&!health.Equals("Unknown",StringComparison.OrdinalIgnoreCase);
  if(unhealthy){problem="Windows reports a warning state for the physical disk containing this volume. Write-based repair is blocked.";confidence=RecoveryConfidence.Low;repair=false;evidence.Add("Hardware warning takes priority over filesystem repair.");}
  else if(bitLockerLocked){problem="The selected volume is BitLocker locked. Repair is blocked until the owner unlocks it with the recovery key or password.";confidence=RecoveryConfidence.High;repair=false;}
  else if(accessible&&!raw){problem="Volume is readable; a non-destructive filesystem scan is appropriate before any repair.";confidence=RecoveryConfidence.High;repair=true;}
  else if(raw){problem="Filesystem is RAW or unrecognized. SmartRAM will not format it or run write-based CHKDSK automatically.";confidence=health.Equals("OK",StringComparison.OrdinalIgnoreCase)?RecoveryConfidence.Medium:RecoveryConfidence.Low;repair=false;evidence.Add("RAW/unrecognized filesystems require recovery-first handling because filesystem repair may alter metadata.");}
  else{problem="Volume is inaccessible or not ready.";confidence=RecoveryConfidence.Low;repair=false;}
  var rec=unhealthy?"Stop repeated repair attempts and create an image/clone before deeper recovery.":repair?"Run a read-only Windows filesystem scan. If corruption is confirmed, SmartRAM can launch the supported repair step after confirmation.":"Do not format or initialize this drive. Recover or image the source first; write-based repair is blocked by SmartRAM.";
  return new DiskRecoveryAssessment(drive,fs,label,cap,free,accessible,health,problem,confidence,string.Join(Environment.NewLine,evidence),rec,repair,bitLockerLocked,mapped.disk);
 },ct);
 public static async Task<string> ScanFileSystemAsync(string drive,CancellationToken ct){
  var psi=new ProcessStartInfo("chkdsk.exe",drive){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
  using var p=Process.Start(psi)??throw new InvalidOperationException("Could not start Windows disk scan.");
  var outputTask=p.StandardOutput.ReadToEndAsync();var errorTask=p.StandardError.ReadToEndAsync();
  try{await p.WaitForExitAsync(ct);}catch(OperationCanceledException){try{if(!p.HasExited)p.Kill(true);}catch{}throw;}
  var output=await outputTask;var err=await errorTask;return string.IsNullOrWhiteSpace(err)?output:output+Environment.NewLine+err;
 }
 public static DiskRepairResult StartWindowsRepair(string drive){
  if(!VirtualMemoryManager.IsAdministrator())return new(false,"Administrator permission is required for filesystem repair.");
  var psi=new ProcessStartInfo("chkdsk.exe",$"{drive} /f"){UseShellExecute=true,Verb="runas"};Process.Start(psi);return new(true,"Windows filesystem repair started. Do not interrupt it.");
 }
}
