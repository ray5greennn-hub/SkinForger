using System;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SkinForger.AnimationAmplitude
{
    public sealed class AnimationAmplitudeSettings
    {
        public const float DefaultParameter = 0f;
        public const float MaximumAbsoluteFactor = 100f;

        public AnimationAmplitudeSettings(float movementAmplitude, float x, float y, float rotate)
        {
            MovementAmplitude = movementAmplitude;
            X = x;
            Y = y;
            Rotate = rotate;
        }

        public float MovementAmplitude { get; private set; }
        public float X { get; private set; }
        public float Y { get; private set; }
        public float Rotate { get; private set; }
        public float OverallMultiplier { get { return 1f + MovementAmplitude; } }
        public float FinalX { get { return OverallMultiplier * (1f + X); } }
        public float FinalY { get { return OverallMultiplier * (1f + Y); } }
        public float FinalRotate { get { return OverallMultiplier * (1f + Rotate); } }

        public bool IsDefault
        {
            get
            {
                return NearlyEqual(MovementAmplitude, 0f) && NearlyEqual(X, 0f) &&
                    NearlyEqual(Y, 0f) && NearlyEqual(Rotate, 0f);
            }
        }

        public static AnimationAmplitudeSettings Default()
        {
            return new AnimationAmplitudeSettings(0f, 0f, 0f, 0f);
        }

        public static AnimationAmplitudeSettings Parse(string json, Action<string> warning)
        {
            if (string.IsNullOrWhiteSpace(json)) return Default();
            try
            {
                JObject root = JObject.Parse(json);
                return new AnimationAmplitudeSettings(
                    ReadFactor(root, "MovementAmplitude", warning),
                    ReadFactor(root, "X", warning),
                    ReadFactor(root, "Y", warning),
                    ReadFactor(root, "Rotate", warning));
            }
            catch (Exception ex)
            {
                if (warning != null) warning("Invalid movement JSON; using defaults: " + ex.Message);
                return Default();
            }
        }

        public static string AddDefaultsToGeneratedJson(string json)
        {
            try
            {
                JObject root = JObject.Parse(json);
                if (root.GetValue("Scale", StringComparison.OrdinalIgnoreCase) == null)
                    root.Add("Scale", 1.0);
                if (root.GetValue("MovementAmplitude", StringComparison.OrdinalIgnoreCase) == null)
                    root.Add("MovementAmplitude", 0.0);
                if (root.GetValue("X", StringComparison.OrdinalIgnoreCase) == null)
                    root.Add("X", 0.0);
                if (root.GetValue("Y", StringComparison.OrdinalIgnoreCase) == null)
                    root.Add("Y", 0.0);
                if (root.GetValue("Rotate", StringComparison.OrdinalIgnoreCase) == null)
                    root.Add("Rotate", 0.0);
                return root.ToString(Formatting.Indented);
            }
            catch
            {
                return json;
            }
        }

        public string Signature()
        {
            return MovementAmplitude.ToString("R", CultureInfo.InvariantCulture) + "," +
                X.ToString("R", CultureInfo.InvariantCulture) + "," +
                Y.ToString("R", CultureInfo.InvariantCulture) + "," +
                Rotate.ToString("R", CultureInfo.InvariantCulture);
        }

        public static bool Same(AnimationAmplitudeSettings left, AnimationAmplitudeSettings right)
        {
            return left != null && right != null &&
                NearlyEqual(left.MovementAmplitude, right.MovementAmplitude) &&
                NearlyEqual(left.X, right.X) && NearlyEqual(left.Y, right.Y) &&
                NearlyEqual(left.Rotate, right.Rotate);
        }

        private static float ReadFactor(JObject root, string name, Action<string> warning)
        {
            JToken token = root.GetValue(name, StringComparison.OrdinalIgnoreCase);
            if (token == null) return DefaultParameter;
            if (token.Type != JTokenType.Float && token.Type != JTokenType.Integer)
            {
                if (warning != null) warning(name + " must be a number; using 0.0.");
                return DefaultParameter;
            }

            double value;
            try
            {
                value = token.Value<double>();
            }
            catch
            {
                if (warning != null) warning(name + " could not be parsed; using 0.0.");
                return DefaultParameter;
            }

            if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > MaximumAbsoluteFactor)
            {
                if (warning != null) warning(name + " is outside the safe range -100..100; using 0.0.");
                return DefaultParameter;
            }
            return (float)value;
        }

        private static bool NearlyEqual(float left, float right)
        {
            return Math.Abs(left - right) < 0.0001f;
        }
    }
}
