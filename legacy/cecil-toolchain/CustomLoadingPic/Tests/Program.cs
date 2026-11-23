using SkinForger;
using Newtonsoft.Json;
using System.IO.Compression;

string root = Path.GetFullPath(Path.Combine("SkinForger_Source", "CustomLoadingPic", "test-results", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
string folder = Path.Combine(root, "CustomLodingPic");
var warnings = new List<string>();
void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }
List<string> Read() => LoadingConfig.Read(folder, warnings.Add);
void Config(string value) => File.WriteAllText(Path.Combine(folder, "config.json"), value);
Check(Read().Count == 0, "missing directory returns empty");
Directory.CreateDirectory(folder);
Check(Read().Count == 0 && File.Exists(Path.Combine(folder, "config.json")), "missing config creates template safely");
Check(Read().Count == 0 && warnings.Count == 3, "missing template images warn without failure");
File.WriteAllBytes(Path.Combine(folder, "1.png"), Png(16, 9, 10, 180, 100));
File.WriteAllBytes(Path.Combine(folder, "2.png"), Png(16, 9, 200, 20, 160));
File.WriteAllBytes(Path.Combine(folder, "unlisted.png"), Png(1, 1, 10, 20, 30));
Config("{\"LoadingPictures\":[\"1.png\",\"2.png\",\"1.PNG\",\"missing.png\",\"../unlisted.png\",\"bad.jpg\",null,1]}");
Check(Read().Count == 2, "allowlist, case-insensitive duplicate suppression, missing file and traversal filtering");
Config("{\"LoadingPictures\":[\"1\",\"2\"]}");
Check(Read().Select(Path.GetFileName).SequenceEqual(new[] { "1.png", "2.png" }), "extensionless names resolve to PNG files");
Config("{\"LoadingPictures\":[\"1.png\",\"2.png\"]}");
Check(Read().Count == 2, "explicit PNG names remain compatible");
Config("{\"LoadingPictures\":[\"1\",\"1.png\"]}");
Check(Read().Count == 1, "extensionless and explicit aliases resolve to one custom entry");
Config("{\"LoadingPictures\":[\"1\",\"missing\"]}");
Check(Read().Count == 1, "one missing entry leaves the valid custom pool active");
var selectionRandom = new Random(774171);
Check(Enumerable.Range(0, 1000).All(_ => LoadingConfig.SelectCustomIndex(1, selectionRandom) == 0), "one valid custom is selected 100 percent");
var selectedIndexes = Enumerable.Range(0, 10000).Select(_ => LoadingConfig.SelectCustomIndex(2, selectionRandom)).ToArray();
Check(selectedIndexes.All(i => i == 0 || i == 1) && selectedIndexes.Contains(0) && selectedIndexes.Contains(1), "two valid customs select only custom indexes");
Check(LoadingConfig.SelectCustomIndex(0, selectionRandom) == -1, "original fallback is reachable only with zero valid customs");
Config("{\"LoadingPictures\":[]}");
Check(Read().Count == 0, "empty array fallback");
Config("   ");
Check(Read().Count == 0, "empty config fallback");
Config("{}");
Check(Read().Count == 0, "missing key fallback");
Config("{broken");
bool malformed = false;
try { Read(); } catch (JsonException) { malformed = true; }
Check(malformed, "malformed JSON reports failure to manager catch/fallback");
Config("{\"LoadingPictures\":42}");
bool wrongType = false;
try { Read(); } catch (FormatException) { wrongType = true; }
Check(wrongType, "wrong config type reports failure to manager catch/fallback");
string old = Path.Combine(root, "Skins");
Directory.CreateDirectory(Path.Combine(old, "Lilith", "elfin_devil", "replaced"));
File.WriteAllText(Path.Combine(old, "Saved.json"), "keep selection");
File.WriteAllText(Path.Combine(old, "Lilith", "elfin_devil", "replaced", "wing_l.json"), "keep history");
string migrated = LoadingPaths.Skins(root);
Check(!Directory.Exists(old) && File.ReadAllText(Path.Combine(migrated, "Saved.json")) == "keep selection", "directory migration preserves selection");
Check(File.ReadAllText(Path.Combine(migrated, "Lilith", "elfin_devil", "replaced", "wing_l.json")) == "keep history", "directory migration preserves backup history");
Check(LoadingPaths.GameRoot(migrated) == root, "backup state stays under game/UserData");
string actual = Environment.CurrentDirectory;
Check(LoadingPaths.NormalizeBackupPath(Path.Combine(actual, "Skins", "Lilith", "x.png")) == Path.Combine(actual, "SkinForger", "Skins", "Lilith", "x.png"), "persisted backup keys migrated without changing hashes");
Directory.CreateDirectory(old);
File.WriteAllText(Path.Combine(old, "untouched.txt"), "legacy conflict");
Check(LoadingPaths.Skins(root) == migrated && File.Exists(Path.Combine(old, "untouched.txt")), "existing target never overwrites legacy conflict");
// Reusable synthetic fixtures, not game asset dumps or edits to the user's skin.
string fixtures = Path.GetFullPath("SkinForger_Source/CustomLoadingPic/test-fixtures");
Directory.CreateDirectory(fixtures);
File.WriteAllBytes(Path.Combine(fixtures, "SF_TEST_GREEN.png"), Png(1280, 720, 25, 180, 95));
File.WriteAllBytes(Path.Combine(fixtures, "SF_TEST_MAGENTA.png"), Png(1280, 720, 195, 35, 150));
File.WriteAllText(Path.Combine(fixtures, "SF_TEST_INVALID.png"), "not a PNG");
Console.WriteLine("Fixtures: " + fixtures);
Console.WriteLine("Configuration/filesystem tests complete; Unity rendering requires the live game.");

foreach (var source in new[] { (4000,4000), (4000,2000), (2000,4000), (1920,1080), (100,80) })
{
    foreach (var target in new[] { (1920,1080), (1024,768), (2560,1080), (800,1200) })
    {
        double sw, sh, mx, my;
        double scale = CoverGeometry.Calculate(source.Item1, source.Item2, target.Item1, target.Item2, out sw, out sh, out mx, out my);
        Check(Math.Abs(sw/source.Item1 - sh/source.Item2) < 1e-9, $"uniform scale {source} -> {target}");
        Check(sw + 1e-8 >= target.Item1 && sh + 1e-8 >= target.Item2, "image covers target without empty edges");
        Check(Math.Abs((sw - 2*mx) - target.Item1) < 1e-8 && Math.Abs((sh - 2*my) - target.Item2) < 1e-8, "symmetric centered crop");
        Check(Math.Abs(sw-target.Item1) < 1e-8 || Math.Abs(sh-target.Item2) < 1e-8, "minimum cover scale");
        Console.WriteLine($"COVER source={source} target={target} scale={scale} scaled={sw}x{sh} crop={mx},{my}");
    }
    File.WriteAllBytes(Path.Combine(fixtures, $"SF_COVER_{source.Item1}x{source.Item2}.png"), CoverPng(source.Item1, source.Item2));
}
foreach (double bad in new[] { 0d, -1d, double.NaN, double.PositiveInfinity })
{
    bool rejected = false;
    try { CoverGeometry.Calculate(100, 80, bad, 1080, out _, out _, out _, out _); }
    catch (ArgumentOutOfRangeException) { rejected = true; }
    Check(rejected, "invalid viewport safely rejected for manager fallback");
}

static byte[] CoverPng(int w, int h)
{
    using var output = new MemoryStream();
    output.Write(new byte[] {137,80,78,71,13,10,26,10});
    byte[] header = new byte[13];
    System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0,4), w);
    System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4,4), h);
    header[8]=8; header[9]=2; Chunk(output,"IHDR",header);
    using var compressed = new MemoryStream();
    using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, true))
    {
        var row = new byte[1+w*3];
        double radius=Math.Min(w,h)*0.22;
        int grid=Math.Max(4, Math.Min(w,h)/10);
        for (int y=0;y<h;y++)
        {
            for (int x=0;x<w;x++)
            {
                double dx=x+0.5-w/2d, dy=y+0.5-h/2d;
                bool circle=dx*dx+dy*dy<=radius*radius;
                bool line=x%grid==0 || y%grid==0;
                int i=1+x*3;
                row[i]=circle?(byte)255:(line?(byte)220:(byte)35);
                row[i+1]=circle?(byte)220:(line?(byte)220:(w>h?(byte)160:(byte)70));
                row[i+2]=circle?(byte)30:(line?(byte)220:(w>h?(byte)100:(byte)185));
            }
            z.Write(row);
        }
    }
    Chunk(output,"IDAT",compressed.ToArray()); Chunk(output,"IEND",Array.Empty<byte>());
    return output.ToArray();
}

static byte[] Png(int w, int h, byte r, byte g, byte b)
{
    using var output = new MemoryStream();
    output.Write(new byte[] {137,80,78,71,13,10,26,10});
    byte[] header = new byte[13];
    System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0,4), w);
    System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4,4), h);
    header[8]=8; header[9]=2;
    Chunk(output,"IHDR",header);
    using var compressed = new MemoryStream();
    using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, true))
        for (int y=0;y<h;y++)
        {
            z.WriteByte(0);
            for (int x=0;x<w;x++)
            {
                bool border=x<24 || y<24 || x>=w-24 || y>=h-24;
                z.WriteByte(border?(byte)255:r); z.WriteByte(border?(byte)255:g); z.WriteByte(border?(byte)255:b);
            }
        }
    Chunk(output,"IDAT",compressed.ToArray()); Chunk(output,"IEND",Array.Empty<byte>());
    return output.ToArray();
}
static void Chunk(Stream s,string name,byte[] data)
{
    byte[] len=new byte[4];
    System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len,data.Length); s.Write(len);
    byte[] type=System.Text.Encoding.ASCII.GetBytes(name); s.Write(type); s.Write(data);
    uint crc=0xffffffff;
    foreach(byte v in type.Concat(data)) { crc^=v; for(int k=0;k<8;k++) crc=(crc&1)!=0?0xedb88320^(crc>>1):crc>>1; }
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len,crc^0xffffffff); s.Write(len);
}
