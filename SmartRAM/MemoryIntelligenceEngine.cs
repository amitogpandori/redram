using System.Diagnostics;
namespace SmartRAM;
public record PressureAssessment(int Score,string Level,string Workload,string Explanation);
public record SmartRecommendation(string Action,double TargetGb,string Confidence,string Reason);
public static class MemoryIntelligenceEngine{
 public static SmartRecommendation Recommend(PcProfile p,PerformanceSnapshot s,double physicalLoad,BenchResult? bench){var a=Assess(s,physicalLoad);var baseRec=RecommendationEngine.Build(p,bench);if(p.RamGb>=32&&a.Score<50)return new("Keep Windows-managed memory",0,"High","Physical memory headroom is sufficient.");if(bench is {ReadMbps:<150}&&a.Score>=50)return new("Prefer a physical RAM/storage upgrade",0,"Medium","Measured storage is relatively slow for paging.");if(a.Score>=50&&p.RamGb<=16&&baseRec.SuggestedGb>0)return new("Increase virtual-memory headroom",baseRec.SuggestedGb,bench is null?"Medium":"High",$"Sustained pressure score is {a.Score}/100. {baseRec.Detail}");if(a.Score>=50&&baseRec.SuggestedGb<=0)return new(baseRec.Title,0,"High",$"SmartRAM remains active. {baseRec.Detail}");return new("Monitor — no change yet",0,"Medium",$"Current pressure score is {a.Score}/100; SmartRAM is monitoring the system and there is not enough evidence for a configuration change.");}

 public static PressureAssessment Assess(PerformanceSnapshot s,double physicalLoad){
  int score=0;
  if(physicalLoad>=90)score+=35; else if(physicalLoad>=80)score+=25; else if(physicalLoad>=70)score+=12;
  if(s.CommitPercent>=90)score+=35; else if(s.CommitPercent>=80)score+=24; else if(s.CommitPercent>=70)score+=12;
  if(s.AvailableMb<512)score+=15; else if(s.AvailableMb<1024)score+=8;
  if(s.PageReadsPerSec>50)score+=10; else if(s.PageReadsPerSec>10)score+=5;
  if(s.DiskLatencyMs>30)score+=10; else if(s.DiskLatencyMs>15)score+=5;
  score=Math.Clamp(score,0,100);
  string level=score>=75?"Critical":score>=50?"High":score>=25?"Moderate":"Healthy";
  string workload=s.CpuPercent>80?"CPU-heavy":s.PageReadsPerSec>25&&physicalLoad>80?"Memory-constrained":s.DiskLatencyMs>25?"Storage-constrained":"Balanced";
  string why=level=="Healthy"?"No sustained memory bottleneck is visible in this sample.":$"{level} pressure: physical load {physicalLoad:F0}%, commit {s.CommitPercent:F0}%, available {s.AvailableMb:F0} MB, page reads {s.PageReadsPerSec:F0}/s, disk latency {s.DiskLatencyMs:F1} ms.";
  return new(score,level,workload,why);
 }
 public static string TopProcesses(int count=5){
  try{return string.Join("\n",Process.GetProcesses().Where(p=>{try{return p.WorkingSet64>0;}catch{return false;}}).OrderByDescending(p=>{try{return p.WorkingSet64;}catch{return 0;}}).Take(count).Select(p=>{try{return $"{p.ProcessName}: {p.WorkingSet64/1048576d:F0} MB";}catch{return p.ProcessName;}}));}catch{return "Process data unavailable";}
 }
}
public sealed class BaselineSession{
 readonly List<PerformanceSnapshot> samples=new(); readonly List<double> loads=new();
 public bool IsRunning{get;private set;} public DateTime Started{get;private set;}
 public void Start(){samples.Clear();loads.Clear();Started=DateTime.Now;IsRunning=true;}
 public void Add(PerformanceSnapshot s,double load){if(IsRunning){samples.Add(s);loads.Add(load);}}
 public string Stop(){IsRunning=false;if(samples.Count==0)return "No samples recorded.";double avgCommit=samples.Average(x=>x.CommitPercent),peakCommit=samples.Max(x=>x.CommitPercent),avgLatency=samples.Average(x=>x.DiskLatencyMs),peakLoad=loads.Max();return $"Recorded {samples.Count} samples\nPeak physical RAM: {peakLoad:F0}%\nAverage / peak commit: {avgCommit:F0}% / {peakCommit:F0}%\nAverage disk latency: {avgLatency:F1} ms\nUse the same workload after optimization for a fair comparison.";}
}