using SkinForger.FpsProtection;

string root = Path.Combine(Path.GetTempPath(), "SkinForgerFpsTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
string config = Path.Combine(root, "SkinForgeFpsLimiter.json");
var warnings = new List<string>();
void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }

Check(FpsProtectionConfig.Load(config, warnings.Add) == 60, "missing config defaults to 60");
Check(File.ReadAllText(config).Contains("\"FPSLimit\": 60"), "default config uses FPSLimit schema");
File.WriteAllText(config, "{\"FPSLimit\":0}");
Check(FpsProtectionConfig.Load(config, warnings.Add) == 0, "FPSLimit zero disables protection");
File.WriteAllText(config, "{\"FPSLimit\":60}");
Check(FpsProtectionConfig.Load(config, warnings.Add) == 60, "FPSLimit 60 loads");
File.WriteAllText(config, "{\"enabled\":true,\"targetFps\":75,\"vSyncCount\":2}");
Check(FpsProtectionConfig.Load(config, warnings.Add) == 75, "legacy enabled target remains compatible and ignores VSync");
File.WriteAllText(config, "{\"enabled\":false,\"targetFps\":60}");
Check(FpsProtectionConfig.Load(config, warnings.Add) == 0, "legacy disabled remains compatible");
File.WriteAllText(config, "{broken");
Check(FpsProtectionConfig.Load(config, warnings.Add) == 60 && warnings.Count > 0, "malformed config safely defaults to 60");
File.WriteAllText(config, "{\"FPSLimit\":999}");
Check(FpsProtectionConfig.Load(config, warnings.Add) == 60, "out of range limit safely defaults to 60");
Check(FpsProtectionConfig.ShouldApplyCap(-1, 60), "unlimited target is capped");
Check(FpsProtectionConfig.ShouldApplyCap(240, 60), "240 is capped to 60");
Check(!FpsProtectionConfig.ShouldApplyCap(60, 60), "60 is unchanged");
Check(!FpsProtectionConfig.ShouldApplyCap(30, 60), "30 is not raised to 60");
Check(!FpsProtectionConfig.ShouldApplyCap(240, 0), "zero disables all writes");
