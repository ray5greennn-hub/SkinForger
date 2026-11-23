using System;

namespace SkinForger
{
    // Independent reference for the runtime Aspect Fill rectangle and centered crop.
    public static class CoverGeometry
    {
        public static double Calculate(double sourceWidth, double sourceHeight, double targetWidth, double targetHeight,
            out double scaledWidth, out double scaledHeight, out double cropX, out double cropY)
        {
            if (!(sourceWidth > 0) || !(sourceHeight > 0) || !(targetWidth > 0) || !(targetHeight > 0) ||
                double.IsInfinity(sourceWidth) || double.IsInfinity(sourceHeight) ||
                double.IsInfinity(targetWidth) || double.IsInfinity(targetHeight))
                throw new ArgumentOutOfRangeException("dimensions", "Cover requires finite positive dimensions.");
            double scale = Math.Max(targetWidth / sourceWidth, targetHeight / sourceHeight);
            scaledWidth = sourceWidth * scale;
            scaledHeight = sourceHeight * scale;
            cropX = Math.Max(0, (scaledWidth - targetWidth) / 2);
            cropY = Math.Max(0, (scaledHeight - targetHeight) / 2);
            return scale;
        }
    }
}
