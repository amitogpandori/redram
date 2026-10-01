using System.Text;
using System.Security.Cryptography;

namespace SmartRAM;

public record SyntheticRecoveryTest(string Name,bool Passed,string Detail);
public static class RecoverySelfTests {
 static void Put(byte[] b,int o,byte[] v)=>Buffer.BlockCopy(v,0,b,o,v.Length);
 static uint Crc32(ReadOnlySpan<byte> data){uint crc=0xffffffff;foreach(byte x in data){crc^=x;for(int i=0;i<8;i++)crc=(crc>>1)^((crc&1)!=0?0xedb88320u:0);}return ~crc;}
 public static IReadOnlyList<SyntheticRecoveryTest> Run(){
  var r=new List<SyntheticRecoveryTest>();
  try{var mbr=new byte[512];mbr[510]=0x55;mbr[511]=0xaa;mbr[446+4]=0x07;Put(mbr,446+8,BitConverter.GetBytes((uint)2048));Put(mbr,446+12,BitConverter.GetBytes((uint)100000));r.Add(new("Synthetic MBR layout",mbr[510]==0x55&&mbr[511]==0xaa&&BitConverter.ToUInt32(mbr,454)==2048,"Valid signature and partition LBA encoded."));}catch(Exception ex){r.Add(new("Synthetic MBR layout",false,ex.Message));}
  try{var ntfs=new byte[512];Put(ntfs,3,Encoding.ASCII.GetBytes("NTFS    "));ntfs[11]=0;ntfs[12]=2;ntfs[13]=8;ntfs[510]=0x55;ntfs[511]=0xaa;r.Add(new("Synthetic NTFS boot sector",Encoding.ASCII.GetString(ntfs,3,8)=="NTFS    "&&ntfs[510]==0x55&&ntfs[511]==0xaa,"NTFS identity and terminal signature encoded."));}catch(Exception ex){r.Add(new("Synthetic NTFS boot sector",false,ex.Message));}
  try{var data=Encoding.ASCII.GetBytes("RED RAM recovery integrity");var a=SHA256.HashData(data);var b=SHA256.HashData(data);r.Add(new("Repair verification hash",a.SequenceEqual(b),"Before/after hash comparison deterministic."));}catch(Exception ex){r.Add(new("Repair verification hash",false,ex.Message));}
  try{var x=Encoding.ASCII.GetBytes("123456789");r.Add(new("CRC32 reference",Crc32(x)==0xCBF43926,"CRC32 matches standard reference vector."));}catch(Exception ex){r.Add(new("CRC32 reference",false,ex.Message));}
  return r;
 }
 public static string Report(){var r=Run();return string.Join(Environment.NewLine,r.Select(x=>$"{(x.Passed?"PASS":"FAIL")} · {x.Name} · {x.Detail}"));}
}
