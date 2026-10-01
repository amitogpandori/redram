using System.Windows;
namespace SmartRAM;
public partial class DiskRecoveryWindow:Window{
 CancellationTokenSource? cts;DiskRecoveryAssessment? assessment;
 public DiskRecoveryWindow(){InitializeComponent();DriveBox.ItemsSource=DiskRecoveryEngine.CandidateDrives();if(DriveBox.Items.Count>0)DriveBox.SelectedIndex=0;}
 async void Scan_Click(object s,RoutedEventArgs e){
  if(DriveBox.SelectedItem is not string drive)return;cts?.Cancel();cts=new();ScanButton.IsEnabled=false;RepairButton.IsEnabled=false;CancelButton.IsEnabled=true;Status.Text="Automatic recovery started — diagnosing safely…";Output.Text="";
  try{
   assessment=await DiskRecoveryEngine.AnalyzeAsync(drive,cts.Token);
   var decision=DiskRecoveryEngine.Decide(assessment);
   Headline.Text=$"{assessment.Drive} · {assessment.Confidence} confidence · {decision.Action}";
   Details.Text=$"Filesystem: {assessment.FileSystem}\nLabel: {assessment.VolumeLabel}\nCapacity: {assessment.Capacity/1073741824d:F1} GB\nAccessible: {assessment.Accessible}\nPhysical disk: {assessment.PhysicalDisk}\nHardware status: {assessment.Health}\nBitLocker locked: {assessment.BitLockerLocked}\n\nDetected problem: {assessment.Problem}\n\nAutomatic path: {decision.Action}\nWhy: {decision.Reason}";
   Evidence.Text=assessment.Evidence;Status.Text="RED RAM selected the safest recovery path automatically…";
   var result=await DiskRecoveryEngine.ExecuteAutomaticAsync(assessment,cts.Token);Output.Text=result.Message;
   if(result.Started){Status.Text="Automatic targeted repair completed. Source was never formatted.";Headline.Text=$"{assessment.Drive} · repair completed";}
   else if(decision.Action==AutoRecoveryAction.ReadOnlyScan){RepairButton.IsEnabled=true;Status.Text="Verification complete. Advanced write repair is available if required.";}
   else if(decision.Action==AutoRecoveryAction.NeedsDestination){Status.Text="Source protected — file recovery requires a different destination drive.";}
   else Status.Text="Automatic diagnosis complete — unsafe writes were blocked.";
  }catch(OperationCanceledException){Status.Text="Recovery cancelled. No further disk changes were made.";}catch(Exception ex){Status.Text="Recovery stopped safely.";Output.Text=ex.Message;}finally{ScanButton.IsEnabled=true;CancelButton.IsEnabled=false;}
 }
 void Structure_Click(object s,RoutedEventArgs e){if(DriveBox.SelectedItem is not string drive)return;var r=DiskRecoveryEngine.InspectPartitionStructures(drive);Output.Text=r.Summary+"\n"+string.Join("\n",r.MbrPartitions.Select(p=>$"Entry {p.Index}: type 0x{p.Type:X2}, start LBA {p.StartLba}, sectors {p.SectorCount}, valid {p.Valid}"));Status.Text="Partition structures inspected read-only.";}
 void Files_Click(object s,RoutedEventArgs e){if(DriveBox.SelectedItem is not string drive)return;using var dlg=new System.Windows.Forms.FolderBrowserDialog{Description="Choose a DIFFERENT drive/folder for recovered files"};if(dlg.ShowDialog()!=System.Windows.Forms.DialogResult.OK)return;cts?.Cancel();cts=new();FilesButton.IsEnabled=false;CancelButton.IsEnabled=true;var p=new Progress<RecoveryProgress>(x=>Status.Text=x.TotalBytes>0?$"Deep recovery {x.BytesProcessed*100d/x.TotalBytes:F1}%":x.Stage);_=RunFilesAsync(drive,dlg.SelectedPath,p,cts.Token);}
 async Task RunFilesAsync(string drive,string dest,IProgress<RecoveryProgress> p,CancellationToken ct){try{var r=await DiskRecoveryEngine.DeepRecoverFilesAsync(drive,dest,p,ct);Output.Text=r.Message;Status.Text=r.Completed?"Deep recovery scan completed.":"Recovery stopped safely.";}finally{FilesButton.IsEnabled=true;CancelButton.IsEnabled=false;}}
 void Image_Click(object s,RoutedEventArgs e){
  if(DriveBox.SelectedItem is not string drive)return;var dlg=new Microsoft.Win32.SaveFileDialog{Title="Save RED RAM recovery image",Filter="Raw disk image (*.img)|*.img",FileName=$"RED-RAM-{drive.Replace(":","")}-recovery.img"};if(dlg.ShowDialog()!=true)return;
  cts?.Cancel();cts=new();ScanButton.IsEnabled=false;ImageButton.IsEnabled=false;CancelButton.IsEnabled=true;Status.Text="Creating read-only recovery image…";
  var progress=new Progress<RecoveryProgress>(p=>{Status.Text=p.TotalBytes>0?$"Imaging {p.BytesProcessed*100d/p.TotalBytes:F1}% · read errors {p.ReadErrors}":p.Stage;});
  _=RunImageAsync(drive,dlg.FileName,progress,cts.Token);
 }
 async Task RunImageAsync(string drive,string path,IProgress<RecoveryProgress> progress,CancellationToken ct){
  try{var r=await DiskRecoveryEngine.CreateRecoveryImageAsync(drive,path,progress,ct);Output.Text=r.Message;Status.Text=r.Completed?"Recovery image completed and source remained read-only.":"Recovery image stopped safely.";}catch(Exception ex){Output.Text=ex.Message;Status.Text="Imaging stopped safely.";}finally{ScanButton.IsEnabled=true;ImageButton.IsEnabled=true;CancelButton.IsEnabled=false;}
 }
 void Cancel_Click(object s,RoutedEventArgs e){cts?.Cancel();CancelButton.IsEnabled=false;Status.Text="Stopping scan safely…";}
 void Repair_Click(object s,RoutedEventArgs e){if(assessment is null||!assessment.CanUseWindowsRepair)return;if(MessageBox.Show($"Repair {assessment.Drive} with Windows CHKDSK /f?\n\nThis writes filesystem metadata. Back up important data first.","Confirm filesystem repair",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;try{var r=DiskRecoveryEngine.StartWindowsRepair(assessment.Drive);Status.Text=r.Message;}catch(Exception ex){MessageBox.Show(ex.Message,"Disk Recovery");}}
 protected override void OnClosed(EventArgs e){cts?.Cancel();cts?.Dispose();base.OnClosed(e);}
}