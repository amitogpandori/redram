using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace SmartRAM;

public enum RecoveryConfidence { Low, Medium, High }
public record DiskRecoveryAssessment(string Drive,string FileSystem,string VolumeLabel,long Capacity,long Free,bool Accessible,string Health,string Problem,RecoveryConfidence Confidence,string Evidence,string Recommendation,bool CanUseWindowsRepair,bool BitLockerLocked,string PhysicalDisk);
public record DiskRepairResult(bool Started,string Message);
public record RawVolumeProbe(bool Opened,string FileSystemHint,string PartitionStyle,string Evidence);
public record NativeRepairPlan(bool SafeToRepair,long BackupOffset,int SectorSize,string Detail);
public enum AutoRecoveryAction { None, ReadOnlyScan, RestoreNtfsBoot, RestoreNtfsMft, NeedsDestination, UnlockBitLocker, ImageFirst }
public record AutoRecoveryDecision(AutoRecoveryAction Action,string Reason);
public record PartitionCandidate(string FileSystem,long Offset,long Size,RecoveryConfidence Confidence,string Evidence);
public record RecoveryProgress(long BytesProcessed,long TotalBytes,int ReadErrors,string Stage);
public record RecoveryJournalEntry(DateTime Timestamp,string Drive,long Offset,int Length,string OriginalSha256,string ReplacementSha256,string Operation,string BackupPath);
public record RecoveryImageResult(bool Completed,string Message,long BytesCopied,int ReadErrors);
public record MbrPartition(int Index,byte Type,long StartLba,long SectorCount,bool Bootable,bool Valid);
public record DiskStructureReport(string Style,bool MbrSignature,bool GptSignature,IReadOnlyList<MbrPartition> MbrPartitions,string Summary);
public record CarvedFile(string Path,string Type,long SourceOffset,long Length);
public record FileRecoveryResult(bool Completed,string Message,int FilesRecovered,long BytesRecovered);

public static class DiskRecoveryEngine {
 const uint GENERIC_READ=0x80000000,GENERIC_WRITE=0x40000000,FILE_SHARE_READ=1,FILE_SHARE_WRITE=2,OPEN_EXISTING=3;
 [DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern SafeFileHandle CreateFile(string name,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
 static string RecoveryRoot { get { var p=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RED RAM","Recovery");Directory.CreateDirectory(p);return p;} }
 static string Sha(byte[] b)=>Convert.ToHexString(SHA256.HashData(b));
 static void Journal(string drive,long offset,byte[] original,byte[] replacement,string operation,string backupPath){
  var e=new RecoveryJournalEntry(DateTime.UtcNow,drive,offset,original.Length,Sha(original),Sha(replacement),operation,backupPath);
  File.AppendAllText(Path.Combine(RecoveryRoot,"repair-journal.jsonl"),JsonSerializer.Serialize(e)+Environment.NewLine);
 }
 static long SafeLength(FileStream fs){try{return fs.Length;}catch{return 0;}}
 public static List<PartitionCandidate> QuickScan(string drive,CancellationToken ct){
  var r=new List<PartitionCandidate>();try{using var h=CreateFile(@"\\.\\"+drive,GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_WRITE,IntPtr.Zero,OPEN_EXISTING,0,IntPtr.Zero);if(h.IsInvalid)return r;using var fs=new FileStream(h,FileAccess.Read,65536,false);var b=new byte[4096];ct.ThrowIfCancellationRequested();int n=fs.Read(b,0,b.Length);if(n>=512){var o=System.Text.Encoding.ASCII.GetString(b,3,8);if(o=="NTFS    ")r.Add(new("NTFS",0,SafeLength(fs),RecoveryConfidence.High,"Valid NTFS OEM signature at volume start."));else if(o.StartsWith("EXFAT"))r.Add(new("exFAT",0,SafeLength(fs),RecoveryConfidence.High,"Valid exFAT signature at volume start."));else if(System.Text.Encoding.ASCII.GetString(b,54,3)=="FAT"||System.Text.Encoding.ASCII.GetString(b,82,3)=="FAT")r.Add(new("FAT",0,SafeLength(fs),RecoveryConfidence.Medium,"FAT signature found at volume start."));}}catch{}return r;
 }
 public static async Task<RecoveryImageResult> CreateRecoveryImageAsync(string drive,string destination,IProgress<RecoveryProgress>? progress,CancellationToken ct){
  var destRoot=Path.GetPathRoot(Path.GetFullPath(destination));if(string.Equals(destRoot,drive+"\\",StringComparison.OrdinalIgnoreCase))return new(false,"Destination cannot be the source volume.",0,0);
  const int block=1024*1024;long done=0,total=0;int errors=0;try{
   using var h=CreateFile(@"\\.\\"+drive,GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_WRITE,IntPtr.Zero,OPEN_EXISTING,0,IntPtr.Zero);if(h.IsInvalid)return new(false,"Could not open source read-only.",0,0);
   using var src=new FileStream(h,FileAccess.Read,block,false);total=SafeLength(src);if(total<=0)return new(false,"Windows did not expose a safe source length for imaging.",0,0);
   Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);using var dst=new FileStream(destination,FileMode.OpenOrCreate,FileAccess.Write,FileShare.Read,block,true);done=Math.Min(dst.Length,total);dst.Position=done;src.Position=done;var buf=new byte[block];
   while(done<total){ct.ThrowIfCancellationRequested();int want=(int)Math.Min(buf.Length,total-done),n=0;try{n=await src.ReadAsync(buf.AsMemory(0,want),ct);}catch(IOException){errors++;src.Position=Math.Min(total,done+want);await dst.WriteAsync(new byte[want],ct);done+=want;progress?.Report(new(done,total,errors,"Imaging around unreadable region"));continue;}if(n<=0)break;await dst.WriteAsync(buf.AsMemory(0,n),ct);done+=n;progress?.Report(new(done,total,errors,"Creating recovery image"));}
   await dst.FlushAsync(ct);return new(done>=total,$"Recovery image {(done>=total?"completed":"stopped")} with {errors} unreadable block(s).",done,errors);
  }catch(OperationCanceledException){return new(false,"Imaging paused/cancelled safely. Existing image is resumable.",done,errors);}catch(Exception ex){return new(false,"Imaging stopped safely: "+ex.Message,done,errors);}
 }

 public static DiskStructureReport InspectPartitionStructures(string drive){
  var parts=new List<MbrPartition>();bool sig=false,gpt=false;try{using var h=CreateFile(@"\\.\\"+drive,GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_WRITE,IntPtr.Zero,OPEN_EXISTING,0,IntPtr.Zero);if(h.IsInvalid)return new("Unknown",false,false,parts,"Native access unavailable.");using var fs=new FileStream(h,FileAccess.Read,4096,false);var b=new byte[1024];if(fs.Read(b,0,b.Length)<512)return new("Unknown",false,false,parts,"Sector 0 unreadable.");sig=b[510]==0x55&&b[511]==0xAA;for(int i=0;i<4;i++){int o=446+i*16;byte type=b[o+4];uint start=BitConverter.ToUInt32(b,o+8),count=BitConverter.ToUInt32(b,o+12);if(type!=0&&count!=0)parts.Add(new(i+1,type,start,count,b[o]==0x80,start>0&&count>0));}if(fs.Length>=1024){fs.Position=512;var g=new byte[512];if(fs.Read(g,0,512)==512)gpt=System.Text.Encoding.ASCII.GetString(g,0,8)=="EFI PART";}var style=gpt?"GPT":parts.Count>0?"MBR":"Unknown";return new(style,sig,gpt,parts,$"{style} detected; {parts.Count} MBR/protective entr{(parts.Count==1?"y":"ies")} parsed read-only.");}catch(Exception ex){return new("Unknown",sig,gpt,parts,"Partition inspection stopped safely: "+ex.Message);}
 }
 static readonly (byte[] Sig,string Ext,string Type)[] CarveSigs={
  (new byte[]{0xFF,0xD8,0xFF},"jpg","Photo"),(new byte[]{0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A},"png","Photo"),(System.Text.Encoding.ASCII.GetBytes("%PDF-"),"pdf","Document"),(new byte[]{0x50,0x4B,0x03,0x04},"zip","Archive")
 };
 public static async Task<FileRecoveryResult> DeepRecoverFilesAsync(string drive,string destination,IProgress<RecoveryProgress>? progress,CancellationToken ct){
  var root=Path.GetPathRoot(Path.GetFullPath(destination));if(string.Equals(root,drive+"\\",StringComparison.OrdinalIgnoreCase))return new(false,"Recovery destination cannot be the source volume.",0,0);Directory.CreateDirectory(destination);const int block=4*1024*1024;long done=0,total=0,recovered=0;int files=0;
  try{using var h=CreateFile(@"\\.\\"+drive,GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_WRITE,IntPtr.Zero,OPEN_EXISTING,0,IntPtr.Zero);if(h.IsInvalid)return new(false,"Could not open source read-only.",0,0);using var src=new FileStream(h,FileAccess.Read,block,false);total=SafeLength(src);if(total<=0)return new(false,"Source length unavailable.",0,0);var buf=new byte[block+16];int carry=0;while(done<total){ct.ThrowIfCancellationRequested();int n;try{n=await src.ReadAsync(buf.AsMemory(carry,block),ct);}catch(IOException){done+=block;src.Position=Math.Min(done,total);carry=0;progress?.Report(new(done,total,1,"Skipping unreadable region"));continue;}if(n<=0)break;int len=carry+n;foreach(var s in CarveSigs){for(int i=0;i<=len-s.Sig.Length;i++){bool match=true;for(int k=0;k<s.Sig.Length;k++)if(buf[i+k]!=s.Sig[k]){match=false;break;}if(!match)continue;long offset=done-carry+i;var name=Path.Combine(destination,$"recovered-{offset:X16}.{s.Ext}");int take=Math.Min(len-i,1024*1024);await File.WriteAllBytesAsync(name,buf.AsMemory(i,take).ToArray(),ct);files++;recovered+=take;i+=take-1;}}carry=Math.Min(15,len);Buffer.BlockCopy(buf,len-carry,buf,0,carry);done+=n;progress?.Report(new(done,total,0,$"Deep recovery · {files} candidate files"));}return new(true,$"Deep signature scan completed. {files} candidate file(s) recovered to a different destination. Carved files may be partial when fragmented.",files,recovered);}catch(OperationCanceledException){return new(false,"Deep recovery cancelled safely.",files,recovered);}catch(Exception ex){return new(false,"Deep recovery stopped safely: "+ex.Message,files,recovered);}
 }
 static RawVolumeProbe ProbeRawVolume(string drive,List<string> evidence){try{using var h=CreateFile(@"\\.\\"+drive,GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_WRITE,IntPtr.Zero,OPEN_EXISTING,0,IntPtr.Zero);if(h.IsInvalid){var msg="Native read-only volume open failed: "+new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;evidence.Add(msg);return new(false,"Unknown","Unknown",msg);}using var fs=new FileStream(h,FileAccess.Read,4096,false);var b=new byte[4096];int n=fs.Read(b,0,b.Length);string hint="Unknown",style="Unknown";if(n>=512){if(b[510]==0x55&&b[511]==0xAA)style="MBR/boot signature present";var oem=System.Text.Encoding.ASCII.GetString(b,3,Math.Min(8,n-3)).Trim();if(oem.Contains("NTFS",StringComparison.OrdinalIgnoreCase))hint="NTFS";else if(oem.Contains("EXFAT",StringComparison.OrdinalIgnoreCase))hint="exFAT";else if(oem.Contains("FAT",StringComparison.OrdinalIgnoreCase))hint="FAT";evidence.Add($"Native read-only probe: {n} bytes read; filesystem hint {hint}; {style}.");}return new(true,hint,style,string.Join(Environment.NewLine,evidence));}catch(Exception ex){evidence.Add("Native read-only probe failed: "+ex.Message);return new(false,"Unknown","Unknown",ex.Message);}}

 static bool ValidNtfsBoot(byte[] b){
  if(b.Length<512||System.Text.Encoding.ASCII.GetString(b,3,8)!="NTFS    "||b[510]!=0x55||b[511]!=0xAA)return false;
  int bps=BitConverter.ToUInt16(b,11),spc=b[13];
  return bps is 512 or 1024 or 2048 or 4096 && spc>0&&(spc&(spc-1))==0;
 }
 static NativeRepairPlan InspectNtfsBackup(string drive,List<string> evidence){
  try{
   using var h=CreateFile(@"\\.\\"+drive,GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_WRITE,IntPtr.Zero,OPEN_EXISTING,0,IntPtr.Zero);
   if(h.IsInvalid)return new(false,0,0,"Native volume access unavailable.");
   using var fs=new FileStream(h,FileAccess.Read,4096,false);long len=fs.Length;
   if(len<4096)return new(false,0,0,"Volume length unavailable or too small.");
   var primary=new byte[512];fs.Position=0;if(fs.Read(primary,0,512)!=512)return new(false,0,0,"Could not read primary boot sector.");
   if(ValidNtfsBoot(primary)){evidence.Add("Primary NTFS boot sector is structurally valid.");return new(false,0,512,"Primary NTFS boot sector is valid.");}
   foreach(var size in new[]{512,4096}){if(len<size)continue;var b=new byte[size];fs.Position=len-size;if(fs.Read(b,0,size)==size&&ValidNtfsBoot(b)){evidence.Add($"Validated NTFS backup boot sector found at offset {len-size}.");return new(true,len-size,size,"Primary NTFS boot sector is invalid but a structurally valid NTFS backup boot sector exists.");}}
   return new(false,0,0,"No validated NTFS backup boot sector was found.");
  }catch(Exception ex){evidence.Add("NTFS backup inspection unavailable: "+ex.Message);return new(false,0,0,ex.Message);}
 }
 public static DiskRepairResult AutoRepairValidatedNtfsBoot(string drive){
  var evidence=new List<string>();var plan=InspectNtfsBackup(drive,evidence);if(!plan.SafeToRepair)return new(false,plan.Detail);
  if(!VirtualMemoryManager.IsAdministrator())return new(false,"Administrator permission is required for validated filesystem repair.");
  try{
   byte[] backup,original;
   using(var h=CreateFile(@"\\.\\"+drive,GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_WRITE,IntPtr.Zero,OPEN_EXISTING,0,IntPtr.Zero)){
    if(h.IsInvalid)return new(false,"Could not open volume read-only.");
    using var fs=new FileStream(h,FileAccess.Read,4096,false);backup=new byte[plan.SectorSize];original=new byte[plan.SectorSize];
    fs.Position=plan.BackupOffset;if(fs.Read(backup,0,backup.Length)!=backup.Length||!ValidNtfsBoot(backup))return new(false,"Backup boot sector changed or failed validation.");
    fs.Position=0;if(fs.Read(original,0,original.Length)!=original.Length)return new(false,"Could not preserve original boot metadata.");
   }
   var dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RED RAM","RecoveryBackups");Directory.CreateDirectory(dir);
   var path=Path.Combine(dir,$"{drive.Replace(":","")}-boot-{DateTime.Now:yyyyMMdd-HHmmss}.bin");File.WriteAllBytes(path,original);
   using var wh=CreateFile(@"\\.\\"+drive,GENERIC_READ|GENERIC_WRITE,FILE_SHARE_READ|FILE_SHARE_WRITE,IntPtr.Zero,OPEN_EXISTING,0,IntPtr.Zero);
   if(wh.IsInvalid)return new(false,"Windows did not grant write access. Original metadata backup was preserved.");
   using var ws=new FileStream(wh,FileAccess.ReadWrite,4096,false);ws.Position=0;ws.Write(backup,0,backup.Length);ws.Flush(true);ws.Position=0;var verify=new byte[backup.Length];if(ws.Read(verify,0,verify.Length)!=verify.Length||!verify.SequenceEqual(backup))return new(false,"Repair write could not be verified. Original metadata backup is preserved.");Journal(drive,0,original,backup,"Restore NTFS backup boot sector",path);
   return new(true,$"Validated NTFS backup boot sector restored and verified. Original metadata was saved to {path}. Reconnect the drive so Windows can remount it.");
  }catch(Exception ex){return new(false,"Automatic validated repair stopped safely: "+ex.Message);}
 }

 static bool ValidMftRecord(byte[] b,int offset,int size){
  if(offset<0||size<512||offset+size>b.Length)return false;
  return b[offset]=='F'&&b[offset+1]=='I'&&b[offset+2]=='L'&&b[offset+3]=='E';
 }
 static NativeRepairPlan InspectNtfsMftMirror(string drive,List<string> evidence){
  try{
   using var h=CreateFile(@"\\.\\"+drive,GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_WRITE,IntPtr.Zero,OPEN_EXISTING,0,IntPtr.Zero);
   if(h.IsInvalid)return new(false,0,0,"Native volume access unavailable.");
   using var fs=new FileStream(h,FileAccess.Read,4096,false);var boot=new byte[512];if(fs.Read(boot,0,512)!=512||!ValidNtfsBoot(boot))return new(false,0,0,"A valid NTFS boot sector is required before MFT recovery.");
   int bps=BitConverter.ToUInt16(boot,11),spc=boot[13];long cluster=(long)bps*spc,mftLcn=BitConverter.ToInt64(boot,48),mirrorLcn=BitConverter.ToInt64(boot,56);sbyte r=unchecked((sbyte)boot[64]);
   int recordSize=r>0?checked((int)(r*cluster)):1<<(-r);if(recordSize<512||recordSize>65536)return new(false,0,0,"NTFS MFT record size is invalid.");
   int records=4,bytes=checked(recordSize*records);long mft=mftLcn*cluster,mirror=mirrorLcn*cluster;if(mft<0||mirror<0||mft+bytes>fs.Length||mirror+bytes>fs.Length)return new(false,0,0,"MFT locations are outside the volume.");
   var a=new byte[bytes];var b=new byte[bytes];fs.Position=mft;if(fs.Read(a,0,bytes)!=bytes)return new(false,0,0,"Could not read primary MFT records.");fs.Position=mirror;if(fs.Read(b,0,bytes)!=bytes)return new(false,0,0,"Could not read MFT mirror records.");
   bool primary=true,backup=true;for(int i=0;i<records;i++){primary&=ValidMftRecord(a,i*recordSize,recordSize);backup&=ValidMftRecord(b,i*recordSize,recordSize);}
   if(primary){evidence.Add("Primary NTFS MFT system records are structurally readable.");return new(false,0,0,"Primary MFT system records are readable.");}
   if(backup){evidence.Add($"Primary MFT system records are damaged; validated MFT mirror found at offset {mirror}.");return new(true,mirror,bytes,$"MFT:{mft}:{mirror}:{bytes}");}
   return new(false,0,0,"Neither the primary MFT system records nor MFT mirror passed structural validation.");
  }catch(Exception ex){evidence.Add("MFT mirror inspection unavailable: "+ex.Message);return new(false,0,0,ex.Message);}
 }
 public static DiskRepairResult AutoRepairValidatedNtfsMft(string drive){
  var ev=new List<string>();var plan=InspectNtfsMftMirror(drive,ev);if(!plan.SafeToRepair)return new(false,plan.Detail);
  if(!VirtualMemoryManager.IsAdministrator())return new(false,"Administrator permission is required for validated MFT repair.");
  try{
   var parts=plan.Detail.Split(':');if(parts.Length!=4||parts[0]!="MFT")return new(false,"MFT repair plan is invalid.");long target=long.Parse(parts[1]),source=long.Parse(parts[2]);int bytes=int.Parse(parts[3]);byte[] mirror,original;
   using(var h=CreateFile(@"\\.\\"+drive,GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_WRITE,IntPtr.Zero,OPEN_EXISTING,0,IntPtr.Zero)){if(h.IsInvalid)return new(false,"Could not reopen volume.");using var fs=new FileStream(h,FileAccess.Read,4096,false);mirror=new byte[bytes];original=new byte[bytes];fs.Position=source;if(fs.Read(mirror,0,bytes)!=bytes)return new(false,"Could not re-read MFT mirror.");fs.Position=target;if(fs.Read(original,0,bytes)!=bytes)return new(false,"Could not preserve damaged MFT records.");}
   var dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RED RAM","RecoveryBackups");Directory.CreateDirectory(dir);var path=Path.Combine(dir,$"{drive.Replace(":","")}-mft-{DateTime.Now:yyyyMMdd-HHmmss}.bin");File.WriteAllBytes(path,original);
   using var wh=CreateFile(@"\\.\\"+drive,GENERIC_READ|GENERIC_WRITE,FILE_SHARE_READ|FILE_SHARE_WRITE,IntPtr.Zero,OPEN_EXISTING,0,IntPtr.Zero);if(wh.IsInvalid)return new(false,"Windows did not grant write access. MFT backup was preserved.");
   using var ws=new FileStream(wh,FileAccess.ReadWrite,4096,false);ws.Position=target;ws.Write(mirror,0,mirror.Length);ws.Flush(true);ws.Position=target;var verify=new byte[mirror.Length];if(ws.Read(verify,0,verify.Length)!=verify.Length||!verify.SequenceEqual(mirror))return new(false,"MFT repair write could not be verified. Original MFT backup is preserved.");Journal(drive,target,original,mirror,"Restore validated NTFS MFT mirror records",path);
   return new(true,$"Validated NTFS MFT mirror restored and verified to the damaged system-record area. Original MFT bytes saved to {path}. Reconnect the drive and verify it before further writes.");
  }catch(Exception ex){return new(false,"Validated MFT repair stopped safely: "+ex.Message);}
 }

 public static AutoRecoveryDecision Decide(DiskRecoveryAssessment a){
  if(a.BitLockerLocked)return new(AutoRecoveryAction.UnlockBitLocker,"BitLocker is locked; recovery requires the owner's key/password.");
  if(!a.Health.Equals("OK",StringComparison.OrdinalIgnoreCase)&&!a.Health.Equals("Unknown",StringComparison.OrdinalIgnoreCase))return new(AutoRecoveryAction.ImageFirst,"Disk health warning detected; write repair is unsafe.");
  if(a.Accessible&&a.CanUseWindowsRepair)return new(AutoRecoveryAction.ReadOnlyScan,"Mounted filesystem: verify it read-only before any repair.");
  var ev=new List<string>();var p=InspectNtfsBackup(a.Drive,ev);
  if(p.SafeToRepair)return new(AutoRecoveryAction.RestoreNtfsBoot,p.Detail);
  var m=InspectNtfsMftMirror(a.Drive,ev);if(m.SafeToRepair)return new(AutoRecoveryAction.RestoreNtfsMft,"Primary MFT system records failed validation while the MFT mirror passed.");
  return new(AutoRecoveryAction.NeedsDestination,"No validated in-place repair is available; preserve the source and recover files to another disk.");
 }
 public static async Task<DiskRepairResult> ExecuteAutomaticAsync(DiskRecoveryAssessment a,CancellationToken ct){
  var d=Decide(a);
  if(d.Action==AutoRecoveryAction.RestoreNtfsBoot)return await Task.Run(()=>AutoRepairValidatedNtfsBoot(a.Drive),ct);
  if(d.Action==AutoRecoveryAction.RestoreNtfsMft)return await Task.Run(()=>AutoRepairValidatedNtfsMft(a.Drive),ct);
  if(d.Action==AutoRecoveryAction.ReadOnlyScan){var o=await ScanFileSystemAsync(a.Drive,ct);return new(false,"Read-only verification completed.\n\n"+o);}
  if(d.Action==AutoRecoveryAction.ImageFirst)return new(false,"Physical-risk path selected automatically. RED RAM will not write to this disk. Image/clone recovery is required before repair.");
  if(d.Action==AutoRecoveryAction.UnlockBitLocker)return new(false,"BitLocker path selected automatically. Unlock the volume with its recovery key/password; RED RAM will not bypass encryption.");
  return new(false,"Recovery-to-another-drive path selected automatically. No safe in-place repair was proven, so the source remains unchanged.");
 }
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
  ct.ThrowIfCancellationRequested();var mapped=PhysicalDiskFor(drive,evidence);var health=mapped.health;var bitLockerLocked=BitLockerLocked(drive,evidence);ct.ThrowIfCancellationRequested();var rawProbe=ProbeRawVolume(drive,evidence);if((string.IsNullOrWhiteSpace(fs)||fs.Equals("Unknown",StringComparison.OrdinalIgnoreCase)||fs.Equals("RAW",StringComparison.OrdinalIgnoreCase))&&rawProbe.FileSystemHint!="Unknown"){fs=rawProbe.FileSystemHint;evidence.Add("Filesystem identified by native read-only signature probe.");}
  var raw=string.IsNullOrWhiteSpace(fs)||fs.Equals("RAW",StringComparison.OrdinalIgnoreCase)||fs.Equals("Unknown",StringComparison.OrdinalIgnoreCase);string problem;RecoveryConfidence confidence;bool repair;
  var unhealthy=!health.Equals("OK",StringComparison.OrdinalIgnoreCase)&&!health.Equals("Unknown",StringComparison.OrdinalIgnoreCase);
  if(unhealthy){problem="Windows reports a warning state for the physical disk containing this volume. Write-based repair is blocked.";confidence=RecoveryConfidence.Low;repair=false;evidence.Add("Hardware warning takes priority over filesystem repair.");}
  else if(bitLockerLocked){problem="The selected volume is BitLocker locked. Repair is blocked until the owner unlocks it with the recovery key or password.";confidence=RecoveryConfidence.High;repair=false;}
  else if(accessible&&!raw){problem="Volume is readable; a non-destructive filesystem scan is appropriate before any repair.";confidence=RecoveryConfidence.High;repair=true;}
  else if(raw){problem="Filesystem is RAW or unrecognized. RED RAM will not format it or run blind write-based CHKDSK.";confidence=health.Equals("OK",StringComparison.OrdinalIgnoreCase)?RecoveryConfidence.Medium:RecoveryConfidence.Low;repair=false;evidence.Add(rawProbe.Opened?"Native read-only access succeeded; deeper recovery can proceed without formatting.":"RAW/unrecognized filesystems require recovery-first handling because filesystem repair may alter metadata.");}
  else{problem="Volume is inaccessible or not ready.";confidence=RecoveryConfidence.Low;repair=false;}
  var rec=unhealthy?"Stop repeated repair attempts and create an image/clone before deeper recovery.":repair?"Run a read-only Windows filesystem scan. If corruption is confirmed, RED RAM can launch the supported repair step after confirmation.":"Do not format or initialize this drive. Recover or image the source first; write-based repair is blocked by RED RAM.";
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
