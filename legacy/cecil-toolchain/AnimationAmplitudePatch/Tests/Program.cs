using SkinForger.AnimationAmplitude;

int failures = 0;
void Check(bool condition, string name)
{
    if (condition) Console.WriteLine("PASS: " + name);
    else { Console.WriteLine("FAIL: " + name); failures++; }
}
bool Near(float a, float b) => Math.Abs(a - b) < 0.0001f;

var defaults = AnimationAmplitudeSettings.Parse("{\"version\":2,\"anchorX\":0,\"anchorY\":136}", null);
Check(defaults.IsDefault && Near(defaults.FinalX, 1) && Near(defaults.FinalY, 1) && Near(defaults.FinalRotate, 1),
    "old JSON defaults to zero parameters and original 1x animation");
Check(Near(RegionScaleSettings.Parse("{\"version\":2}", null), 1),
    "old JSON defaults to image Scale 1x");
Check(Near(RegionScaleSettings.Parse("{\"Scale\":2.0}", null), 2) &&
      Near(RegionScaleSettings.Parse("{\"Scale\":3.0}", null), 3) &&
      Near(RegionScaleSettings.Parse("{\"Scale\":0.5}", null), 0.5f),
    "image Scale supports positive fractional values");

var scaleWarnings = new List<string>();
Check(Near(RegionScaleSettings.Parse("{\"Scale\":null}", scaleWarnings.Add), 1) &&
      Near(RegionScaleSettings.Parse("{\"Scale\":\"large\"}", scaleWarnings.Add), 1) &&
      Near(RegionScaleSettings.Parse("{\"Scale\":0}", scaleWarnings.Add), 1) &&
      Near(RegionScaleSettings.Parse("{\"Scale\":-2}", scaleWarnings.Add), 1),
    "invalid nonpositive image Scale values safely default to 1x");

var allDouble = AnimationAmplitudeSettings.Parse(
    "{\"MovementAmplitude\":1.0,\"X\":0.0,\"Y\":0.0,\"Rotate\":0.0}", null);
Check(Near(allDouble.FinalX, 2) && Near(allDouble.FinalY, 2) && Near(allDouble.FinalRotate, 2),
    "overall amplitude doubles X Y rotation");

var allOneAndHalf = AnimationAmplitudeSettings.Parse(
    "{\"MovementAmplitude\":0.5,\"X\":0.0,\"Y\":0.0,\"Rotate\":0.0}", null);
Check(Near(allOneAndHalf.FinalX, 1.5f) && Near(allOneAndHalf.FinalY, 1.5f) &&
      Near(allOneAndHalf.FinalRotate, 1.5f), "overall amplitude supports 1.5x");

var stopped = AnimationAmplitudeSettings.Parse(
    "{\"MovementAmplitude\":-1.0,\"X\":0.0,\"Y\":0.0,\"Rotate\":0.0}", null);
Check(Near(stopped.FinalX, 0) && Near(stopped.FinalY, 0) && Near(stopped.FinalRotate, 0),
    "minus one overall amplitude disables movement deltas");

var independent = AnimationAmplitudeSettings.Parse(
    "{\"MovementAmplitude\":0.0,\"X\":1.0,\"Y\":-0.5,\"Rotate\":0.5}", null);
Check(Near(independent.FinalX, 2) && Near(independent.FinalY, 0.5f) && Near(independent.FinalRotate, 1.5f),
    "independent axis parameters are relative adjustments");

var combined = AnimationAmplitudeSettings.Parse(
    "{\"MovementAmplitude\":0.5,\"X\":1.0,\"Y\":0.0,\"Rotate\":-1.0}", null);
Check(Near(combined.FinalX, 3) && Near(combined.FinalY, 1.5f) && Near(combined.FinalRotate, 0),
    "global and axis factors multiply");

var reversed = AnimationAmplitudeSettings.Parse(
    "{\"MovementAmplitude\":0.0,\"X\":-2.0,\"Y\":0.0,\"Rotate\":-2.0}", null);
Check(Near(reversed.FinalX, -1) && Near(reversed.FinalY, 1) && Near(reversed.FinalRotate, -1),
    "minus two axis parameters reverse X and rotation");

var floatValues = AnimationAmplitudeSettings.Parse(
    "{\"MovementAmplitude\":0.25,\"X\":-0.25,\"Y\":0.5,\"Rotate\":-0.5}", null);
Check(Near(floatValues.FinalX, 0.9375f) && Near(floatValues.FinalY, 1.875f) && Near(floatValues.FinalRotate, 0.625f),
    "fractional factors supported");

var warnings = new List<string>();
var invalid = AnimationAmplitudeSettings.Parse(
    "{\"MovementAmplitude\":null,\"X\":\"x2\",\"Y\":1000,\"Rotate\":0}", warnings.Add);
Check(Near(invalid.MovementAmplitude, 0) && Near(invalid.X, 0) && Near(invalid.Y, 0) && Near(invalid.Rotate, 0),
    "null string and extreme values safely default");
Check(warnings.Count == 3, "invalid values emit bounded warnings");

float[] translate = { 0f, 10f, -20f, 1f, -3f, 8f };
float[] scaledTranslate = AnimationAmplitudeMath.ScaleTranslateFrames(translate, independent);
Check(Near(scaledTranslate[0], 0) && Near(scaledTranslate[3], 1), "translate keyframe times unchanged");
Check(Near(scaledTranslate[1], 20) && Near(scaledTranslate[2], -10) &&
      Near(scaledTranslate[4], -6) && Near(scaledTranslate[5], 4),
    "translate deltas scaled independently");
Check(Near(translate[1], 10) && Near(translate[2], -20), "original translate frames preserved");

float[] rotate = { 0f, 15f, 1f, -30f };
float[] scaledRotate = AnimationAmplitudeMath.ScaleRotateFrames(rotate, reversed);
Check(Near(scaledRotate[0], 0) && Near(scaledRotate[2], 1), "rotation keyframe times unchanged");
Check(Near(scaledRotate[1], -15) && Near(scaledRotate[3], 30), "rotation deltas reversed");

string generated = AnimationAmplitudeSettings.AddDefaultsToGeneratedJson(
    "{\"version\":2,\"origin\":\"top-left\",\"anchorX\":0,\"anchorY\":136,\"rotation\":0}");
Check(generated.Contains("\"Scale\": 1.0") && generated.Contains("\"MovementAmplitude\": 0.0") && generated.Contains("\"X\": 0.0") &&
      generated.Contains("\"Y\": 0.0") && generated.Contains("\"Rotate\": 0.0"),
    "newly generated sidecars include Scale and movement defaults");
Check(generated.IndexOf("\"rotation\"", StringComparison.Ordinal) < generated.IndexOf("\"Scale\"", StringComparison.Ordinal) &&
      generated.IndexOf("\"Scale\"", StringComparison.Ordinal) < generated.IndexOf("\"MovementAmplitude\"", StringComparison.Ordinal),
    "generated sidecar writes Scale between rotation and movement fields");

return failures == 0 ? 0 : 1;
