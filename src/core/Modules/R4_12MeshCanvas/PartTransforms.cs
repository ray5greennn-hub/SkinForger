using System.Globalization;
using System.Text.Json;
using Il2CppSpine;
using SkinForger.R47.Core;
using UnityEngine;

namespace SkinForger.R412.Runtime;

/// <summary>
/// JSON offsetX / offsetY / rotation of a MESH part = move / turn the whole part rigidly (2026-11-16, user: "if it is
/// only moved in x/y or rotated, the mesh deformation must be exactly the same as the original").
///
/// Before: an offset moved the drawing relative to the mesh (OffsetCanvas padding + R4-12 band), so the moved part was
/// carried by the band and swung differently. Now the offset no longer enters the canvas spec at all; instead, every
/// frame, after Spine evaluated the deformed mesh (MeshGenerator.FillVertexData prefix, transient buffers only - no
/// attachment is written), the part's vertices (and its R4-12 band vertices) get one rigid transform:
///     v' = c + Rot(±R)·(v - c) + M·d
/// where M is the least-squares affine map from the part's image (pixels, y down) to its current deformed vertices,
/// d = (offsetX, offsetY) pixels, R = rotation (degrees, positive = clockwise on screen) and c = M(pivot), pivot = the
/// part's anchor point in the original image (Main's DefaultAnchor). The deformed shape is kept exactly; it is only
/// shifted / turned with the part's current frame.
/// Only drawn from SkinForger's runtime atlases (Original mode = game atlases: untouched). Elfins / region parts:
/// unchanged (Main moves region quads by offset itself).
/// </summary>
internal static class PartTransforms
{
    internal sealed record Entry(string Name, double Dx, double Dy, double RotationDeg, double PivotX, double PivotY, double Width, double Height)
    {
        internal string Describe() => $"{Name} offset=({Dx:0.##},{Dy:0.##}) rotation={RotationDeg:0.##} pivot=({PivotX:0.##},{PivotY:0.##}) image={Width:0}x{Height:0}";
    }

    private static readonly Dictionary<long, Entry> Entries = new();
    private static volatile int _count;
    internal static bool Any => _count > 0;
    internal static Action<string, string>? Log;
    private static readonly HashSet<string> Logged = new();

    internal static void Clear() { lock (Entries) { Entries.Clear(); _count = 0; } }

    internal static bool TryGet(long attachment, out Entry entry)
    {
        lock (Entries) return Entries.TryGetValue(attachment, out entry!);
    }

    /// <summary>From Main's placement of a mesh attachment (+ "rotation" from its JSON).</summary>
    internal static void Register(MeshAttachment mesh, string name, object placement, string? regionPath)
    {
        double R(string p) { try { return Convert.ToDouble(placement.GetType().GetProperty(p)?.GetValue(placement) ?? 0, CultureInfo.InvariantCulture); } catch { return 0; } }
        double dx = R("OffsetX"), dy = R("OffsetY");
        double rotation = ReadRotation(regionPath);
        double w = R("BaseNormalWidth"), h = R("BaseNormalHeight");
        long key = mesh.Pointer.ToInt64();
        lock (Entries)
        {
            if (Math.Abs(dx) < 1e-6 && Math.Abs(dy) < 1e-6 && Math.Abs(rotation) < 1e-6 || w <= 0 || h <= 0)
                Entries.Remove(key);
            else
            {
                var entry = new Entry(name, dx, dy, rotation, R("DefaultAnchorX"), R("DefaultAnchorY"), w, h);
                Entries[key] = entry;
                string text = entry.Describe();
                if (Logged.Add(text)) Log?.Invoke("PART_TRANSFORM", text + " (whole part moved / turned, deformation unchanged)");
            }
            _count = Entries.Count;
        }
    }

    private static double ReadRotation(string? regionPath)
    {
        try
        {
            if (string.IsNullOrEmpty(regionPath)) return 0;
            string json = Path.ChangeExtension(regionPath, ".json");
            if (!global::SkinForger.SkinPackages.VirtualFiles.FileExists(json)) return 0;
            using JsonDocument doc = JsonDocument.Parse(global::SkinForger.SkinPackages.VirtualFiles.ReadAllText(json));
            // The Dump writes both "rotation" and "Rotate" (2026-11-16: the user edited "Rotate": 30 and nothing turned).
            // Both count (added together).
            double Read(string name)
            {
                if (!doc.RootElement.TryGetProperty(name, out JsonElement v)) return 0;
                double value = v.ValueKind switch
                {
                    JsonValueKind.Number => v.GetDouble(),
                    JsonValueKind.String => double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0,
                    _ => 0
                };
                return double.IsFinite(value) ? value : 0;
            }
            return Read("rotation") + Read("Rotate");
        }
        catch { return 0; }
    }

    /// <summary>Rigid transform of one part. positions: the part's native vertices; extra: its band vertices (may be empty).</summary>
    internal static bool Apply(Entry e, V2[] regionUv, Span<Vector3> native, Span<Vector3> band, ref Vector2 min, ref Vector2 max)
    {
        int n = regionUv.Length;
        if (n < 3 || native.Length != n) return false;
        // Least squares: x' = a·x + b·y + tx, y' = c·x + d·y + ty, image pixels (x right, y down) -> local vertices.
        double sxx = 0, sxy = 0, syy = 0, sx = 0, sy = 0;
        double bx0 = 0, bx1 = 0, bx2 = 0, by0 = 0, by1 = 0, by2 = 0;
        for (int i = 0; i < n; i++)
        {
            double x = regionUv[i].X * e.Width, y = regionUv[i].Y * e.Height;
            double X = native[i].x, Y = native[i].y;
            sxx += x * x; sxy += x * y; syy += y * y; sx += x; sy += y;
            bx0 += x * X; bx1 += y * X; bx2 += X;
            by0 += x * Y; by1 += y * Y; by2 += Y;
        }
        if (!Solve3(sxx, sxy, sx, sxy, syy, sy, sx, sy, n, bx0, bx1, bx2, out double a, out double b, out double tx)) return false;
        if (!Solve3(sxx, sxy, sx, sxy, syy, sy, sx, sy, n, by0, by1, by2, out double c, out double d, out double ty)) return false;
        double det = a * d - b * c;
        if (Math.Abs(det) < 1e-12) return false;

        // Translation M·delta
        double shiftX = a * e.Dx + b * e.Dy, shiftY = c * e.Dx + d * e.Dy;
        // Rotation: an exact rigid turn of the deformed part about its pivot. Image y points down, local y up, so a
        // turn that is clockwise in the image is clockwise on screen: angle -R when the map flips (det < 0, normal),
        // +R when the part is mirrored (det > 0).
        double q00 = 1, q01 = 0, q10 = 0, q11 = 1, cx = 0, cy = 0;
        if (Math.Abs(e.RotationDeg) > 1e-6)
        {
            double t = (det < 0 ? -1 : 1) * e.RotationDeg * Math.PI / 180.0, cs = Math.Cos(t), sn = Math.Sin(t);
            q00 = cs; q01 = -sn; q10 = sn; q11 = cs;
            cx = a * e.PivotX + b * e.PivotY + tx;
            cy = c * e.PivotX + d * e.PivotY + ty;
        }
        if (!double.IsFinite(shiftX + shiftY + q00 + q01 + q10 + q11 + cx + cy)) return false;

        void Move(Span<Vector3> span, ref Vector2 lo, ref Vector2 hi)
        {
            for (int i = 0; i < span.Length; i++)
            {
                ref Vector3 p = ref span[i];
                double rx = p.x - cx, ry = p.y - cy;
                float x = (float)(cx + q00 * rx + q01 * ry + shiftX), y = (float)(cy + q10 * rx + q11 * ry + shiftY);
                p.x = x; p.y = y;
                if (x < lo.x) lo.x = x; if (y < lo.y) lo.y = y;
                if (x > hi.x) hi.x = x; if (y > hi.y) hi.y = y;
            }
        }
        Move(native, ref min, ref max);
        Move(band, ref min, ref max);
        return true;
    }

    /// <summary>Solves the symmetric 3x3 system [m]·(p, q, r) = (u, v, w) (Cramer).</summary>
    private static bool Solve3(double m00, double m01, double m02, double m10, double m11, double m12, double m20, double m21, double m22,
                               double u, double v, double w, out double p, out double q, out double r)
    {
        double det = m00 * (m11 * m22 - m12 * m21) - m01 * (m10 * m22 - m12 * m20) + m02 * (m10 * m21 - m11 * m20);
        p = q = r = 0;
        if (Math.Abs(det) < 1e-9) return false;
        p = (u * (m11 * m22 - m12 * m21) - m01 * (v * m22 - m12 * w) + m02 * (v * m21 - m11 * w)) / det;
        q = (m00 * (v * m22 - m12 * w) - u * (m10 * m22 - m12 * m20) + m02 * (m10 * w - v * m20)) / det;
        r = (m00 * (m11 * w - v * m21) - m01 * (m10 * w - v * m20) + u * (m10 * m21 - m11 * m20)) / det;
        return true;
    }
}
