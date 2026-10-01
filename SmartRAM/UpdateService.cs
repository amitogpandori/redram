using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace RED RAM;

public record UpdateCheckResult(bool UpdateAvailable,Version Current,Version? Latest,string Message,string? ReleaseUrl);

public static class UpdateService {
 const string ReleasesApi="https://api.github.com/repos/amitogpandori/redram/releases/latest";
 static readonly HttpClient Client=CreateClient();
 static HttpClient CreateClient(){var c=new HttpClient{Timeout=TimeSpan.FromSeconds(8)};c.DefaultRequestHeaders.UserAgent.ParseAdd("RED-RAM-Windows-Updater/0.9");c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");return c;}
 public static async Task<UpdateCheckResult> CheckAsync(CancellationToken ct=default){
  var current=Assembly.GetExecutingAssembly().GetName().Version??new Version(0,0);
  try{
   using var response=await Client.GetAsync(ReleasesApi,ct);
   if(!response.IsSuccessStatusCode)return new(false,current,null,"Update service is not available yet. RED RAM will support public release updates when the release channel is enabled.",null);
   await using var stream=await response.Content.ReadAsStreamAsync(ct);using var json=await JsonDocument.ParseAsync(stream,cancellationToken:ct);
   var tag=json.RootElement.GetProperty("tag_name").GetString()?.Trim().TrimStart('v','V');
   var url=json.RootElement.TryGetProperty("html_url",out var u)?u.GetString():null;
   if(!Version.TryParse(tag,out var latest))return new(false,current,null,"The update server returned an unknown version.",url);
   return latest>current?new(true,current,latest,$"RED RAM {latest} is available.",url):new(false,current,latest,"You already have the latest RED RAM version.",url);
  }catch(OperationCanceledException){throw;}catch(Exception ex){return new(false,current,null,"Could not check for updates: "+ex.Message,null);}
 }
 public static void OpenRelease(string url){Process.Start(new ProcessStartInfo(url){UseShellExecute=true});}
}
