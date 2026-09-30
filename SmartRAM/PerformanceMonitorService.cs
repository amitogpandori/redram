using System.Diagnostics;
namespace SmartRAM;
public record PerformanceSnapshot(DateTime Time,double CommitPercent,double AvailableMb,double PagesPerSec,double PageReadsPerSec,double PageWritesPerSec,double DiskLatencyMs,double CpuPercent);
public sealed class PerformanceMonitorService:IDisposable{
 readonly List<PerformanceCounter> counters=new();
 PerformanceCounter C(string cat,string name,string inst=""){var c=new PerformanceCounter(cat,name,inst,true);c.NextValue();counters.Add(c);return c;}
 readonly PerformanceCounter avail,commit,pages,reads,writes,disk,cpu;
 public PerformanceMonitorService(){avail=C("Memory","Available MBytes");commit=C("Memory","% Committed Bytes In Use");pages=C("Memory","Pages/sec");reads=C("Memory","Page Reads/sec");writes=C("Memory","Page Writes/sec");disk=C("PhysicalDisk","Avg. Disk sec/Transfer","_Total");cpu=C("Processor","% Processor Time","_Total");}
 public PerformanceSnapshot Sample()=>new(DateTime.Now,commit.NextValue(),avail.NextValue(),pages.NextValue(),reads.NextValue(),writes.NextValue(),disk.NextValue()*1000,cpu.NextValue());
 public void Dispose(){foreach(var c in counters)c.Dispose();}
}