using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SmartRAM;

public enum RescueRisk { Normal, ReadErrors, Failing }
public record NativeVolumeInfo(string Path,long Length,uint BytesPerSector,string PhysicalDisk,bool Available,string Evidence);
public record GptReport(bool Present,bool HeaderValid,bool HeaderCrcValid,bool EntriesCrcValid,ulong CurrentLba,ulong BackupLba,ulong FirstUsableLba,ulong LastUsableLba,uint EntryCount,uint EntrySize,string Evidence);
public record MbrEntry(int Index,byte Type,uint StartLba,uint SectorCount,bool Bootable);
public record PartitionScanHit(long Offset,string FileSystem,int Score,string Evidence);
public record ImageMapRange(long Offset,int Length,bool Good);
public record RescueImageResult(bool Completed,long BytesCopied,int BadBlocks,string ImagePath,string MapPath,string Message);

public static class AdvancedRecoveryEngine {
 const uint GENERIC_READ=0x80000000,FILE_SHARE_READ=1,FILE_SHARE_WRITE=2,OPEN_EXISTING=3;
 const uint IOCTL_DISK_GET_LENGTH_INFO=0x0007405C,IOCTL_DISK_GET_DRIVE_GEOMETRY=0x00070000;
 [DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern SafeFileHandle CreateFile(string n,uint a,uint s,IntPtr sa,uint c,uint f,IntPtr t);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool DeviceIoControl(SafeFileHandle h,uint code,IntPtr input,uint inSize,IntPtr output,uint outSize,out uint returned,IntPtr overlapped);
 static SafeFileHandle OpenRead(string drive)=>CreateFile(@"\\.\\"+drive.TrimEnd('\\'),GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_WRITE,IntPtr.Zero,OPEN_EXISTING,0,IntPtr.Zero);
 public static NativeVolumeInfo GetNativeVolumeInfo(string drive){
  try{using var h=OpenRead(drive);if(h.IsInvalid)return new(drive,0,512,"Unknown",false,"Native open failed.");
   long len=0;uint bps=512,ret;IntPtr p=Marshal.AllocHGlobal(32);try{Marshal.WriteInt64(p,0);if(DeviceIoControl(h,IOCTL_DISK_GET_LENGTH_INFO,IntPtr.Zero,0,p,8,out ret,IntPtr.Zero))len=Marshal.ReadInt64(p);if(DeviceIoControl(h,IOCTL_DISK_GET_DRIVE_GEOMETRY,IntPtr.Zero,0,p,24,out ret,IntPtr.Zero))bps=(uint)Marshal.ReadInt32(p,20);}finally{Marshal.FreeHGlobal(p);}
   return new(drive,len,bps,"Native mapped volume",len>0,$"Native length {len} bytes; sector size {bps}.");}catch(Exception ex){return new(drive,0,512,"Unknown",false,ex.Message);}
 }
 static uint Crc32(ReadOnlySpan<byte> data){uint crc=0xffffffff;foreach(byte b in data){crc^=b;for(int i=0;i<8;i++)crc=(crc>>1)^((crc&1)!=0?0xedb88320u:0);}return ~crc;}
 public static GptReport InspectGpt(string drive){
  var info=GetNativeVolumeInfo(drive);if(!info.Available)return new(false,false,false,false,0,0,0,0,0,0,info.Evidence);
  try{using var h=OpenRead(drive);using var fs=new FileStream(h,FileAccess.Read,4096,false);var sec=new byte[Math.Max(512,(int)info.BytesPerSector)];fs.Position=info.BytesPerSector;if(fs.Read(sec,0,sec.Length)<92||Encoding.ASCII.GetString(sec,0,8)!="EFI PART")return new(false,false,false,false,0,0,0,0,0,0,"No primary GPT header.");
   uint headerSize=BitConverter.ToUInt32(sec,12),stored=BitConverter.ToUInt32(sec,16);if(headerSize<92||headerSize>sec.Length)return new(true,false,false,false,0,0,0,0,0,0,"GPT header size invalid.");
   var copy=sec.AsSpan(0,(int)headerSize).ToArray();Array.Clear(copy,16,4);bool hcrc=Crc32(copy)==stored;ulong cur=BitConverter.ToUInt64(sec,24),bak=BitConverter.ToUInt64(sec,32),first=BitConverter.ToUInt64(sec,40),last=BitConverter.ToUInt64(sec,48),entryLba=BitConverter.ToUInt64(sec,72);uint count=BitConverter.ToUInt32(sec,80),size=BitConverter.ToUInt32(sec,84),entriesCrc=BitConverter.ToUInt32(sec,88);
   bool bounds=cur==1&&bak>cur&&first<=last&&last*info.BytesPerSector<(ulong)info.Length&&count>0&&count<=16384&&size>=128&&size<=4096;bool ecrc=false;
   ulong bytes=(ulong)count*size;if(bounds&&bytes<=64*1024*1024&&entryLba*info.BytesPerSector+bytes<=(ulong)info.Length){var entries=new byte[(int)bytes];fs.Position=(long)(entryLba*info.BytesPerSector);int got=0;while(got<entries.Length){int n=fs.Read(entries,got,entries.Length-got);if(n<=0)break;got+=n;}ecrc=got==entries.Length&&Crc32(entries)==entriesCrc;}
   return new(true,bounds,hcrc,ecrc,cur,bak,first,last,count,size,$"GPT primary header: bounds={bounds}, header CRC={hcrc}, entry-array CRC={ecrc}.");}catch(Exception ex){return new(false,false,false,false,0,0,0,0,0,0,"GPT inspection failed safely: "+ex.Message);}
 }
 public static IReadOnlyList<MbrEntry> InspectMbr(string drive){
  var r=new List<MbrEntry>();try{using var h=OpenRead(drive);using var fs=new FileStream(h,FileAccess.Read,4096,false);var b=new byte[512];if(fs.Read(b,0,512)!=512||b[510]!=0x55||b[511]!=0xaa)return r;for(int i=0;i<4;i++){int o=446+i*16;byte type=b[o+4];uint start=BitConverter.ToUInt32(b,o+8),count=BitConverter.ToUInt32(b,o+12);if(type!=0&&count!=0)r.Add(new(i+1,type,start,count,b[o]==0x80));}}catch{}return r;
 }
 static string DetectFs(byte[] b,int n){if(n>=512&&Encoding.ASCII.GetString(b,3,8)=="NTFS    "&&b[510]==0x55&&b[511]==0xaa)return "NTFS";if(n>=11&&Encoding.ASCII.GetString(b,3,8)=="EXFAT   ")return "exFAT";if(n>=90&&(Encoding.ASCII.GetString(b,54,3)=="FAT"||Encoding.ASCII.GetString(b,82,3)=="FAT"))return "FAT";return "";}
 public static async Task<IReadOnlyList<PartitionScanHit>> DeepPartitionScanAsync(string drive,IProgress<RecoveryProgress>? progress,CancellationToken ct){
  var hits=new List<PartitionScanHit>();var info=GetNativeVolumeInfo(drive);if(!info.Available)return hits;const int chunk=4*1024*1024;using var h=OpenRead(drive);using var fs=new FileStream(h,FileAccess.Read,chunk,false);var b=ArrayPool<byte>.Shared.Rent(chunk);try{long pos=0;while(pos<info.Length){ct.ThrowIfCancellationRequested();int want=(int)Math.Min(chunk,info.Length-pos),n=0;try{n=await fs.ReadAsync(b.AsMemory(0,want),ct);}catch(IOException){pos+=want;fs.Position=Math.Min(pos,info.Length);continue;}if(n<=0)break;for(int i=0;i+512<=n;i+=(int)info.BytesPerSector){var fsn=DetectFs(b.AsSpan(i,Math.Min(4096,n-i)).ToArray(),Math.Min(4096,n-i));if(fsn.Length>0){long off=pos+i;if(!hits.Any(x=>Math.Abs(x.Offset-off)<(long)info.BytesPerSector*8))hits.Add(new(off,fsn,90,$"{fsn} boot signature at byte {off}."));}}pos+=n;progress?.Report(new(pos,info.Length,0,$"Deep partition scan · {hits.Count} candidate(s)"));}}finally{ArrayPool<byte>.Shared.Return(b);}return hits.OrderBy(x=>x.Offset).ToList();
 }
 public static async Task<RescueImageResult> RescueImageAsync(string drive,string imagePath,IProgress<RecoveryProgress>? progress,CancellationToken ct){
  var info=GetNativeVolumeInfo(drive);if(!info.Available)return new(false,0,0,imagePath,imagePath+".map","Native source length unavailable.");var root=Path.GetPathRoot(Path.GetFullPath(imagePath));if(string.Equals(root,drive+"\\",StringComparison.OrdinalIgnoreCase))return new(false,0,0,imagePath,imagePath+".map","Destination must not be the source volume.");
  string map=imagePath+".map";long done=0;int bad=0;const int block=1024*1024;using var h=OpenRead(drive);using var src=new FileStream(h,FileAccess.Read,block,false);Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(imagePath))!);using var dst=new FileStream(imagePath,FileMode.OpenOrCreate,FileAccess.Write,FileShare.Read,block,true);done=Math.Min(dst.Length,info.Length);src.Position=done;dst.Position=done;var buf=ArrayPool<byte>.Shared.Rent(block);try{while(done<info.Length){ct.ThrowIfCancellationRequested();int want=(int)Math.Min(block,info.Length-done),n=0;bool good=true;try{n=await src.ReadAsync(buf.AsMemory(0,want),ct);}catch(IOException){good=false;n=want;Array.Clear(buf,0,want);src.Position=Math.Min(info.Length,done+want);bad++;}if(n<=0)break;await dst.WriteAsync(buf.AsMemory(0,n),ct);await File.AppendAllTextAsync(map,$"{done},{n},{(good?"GOOD":"BAD")}\n",ct);done+=n;progress?.Report(new(done,info.Length,bad,good?"Imaging readable sectors":"Preserving position of unreadable sectors"));}await dst.FlushAsync(ct);return new(done>=info.Length,done,bad,imagePath,map,$"Image {(done>=info.Length?"completed":"stopped")}; {bad} unreadable block(s).");}catch(OperationCanceledException){return new(false,done,bad,imagePath,map,"Imaging cancelled safely; image and map can be resumed.");}finally{ArrayPool<byte>.Shared.Return(buf);}
 }
 public static bool IsDifferentDestination(string sourceDrive,string destination){try{return !string.Equals(Path.GetPathRoot(Path.GetFullPath(destination)),sourceDrive+"\\",StringComparison.OrdinalIgnoreCase);}catch{return false;}}
}
