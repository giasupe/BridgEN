using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using WinRT;

namespace BridgEN.App.Ocr;

/// <summary>Creates a high-contrast, enlarged image for a second OCR pass.</summary>
internal static class OcrImagePreprocessor
{
    private const int MaximumScale = 2;
    private const int MaximumOcrImageDimension = 4096;
    private const int BorderPixels = 5;

    public static OcrFrameSignature CreateFrameSignature(SoftwareBitmap source)
    {
        using var converted = source.BitmapPixelFormat == BitmapPixelFormat.Bgra8
            ? null
            : SoftwareBitmap.Convert(source, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var input = converted ?? source;
        return OcrFrameSignature.Create(ReadPixels(input), input.PixelWidth, input.PixelHeight);
    }

    public static SoftwareBitmap CreateRecognitionBitmap(SoftwareBitmap source, OcrImageTreatment treatment) =>
        treatment switch
        {
            OcrImageTreatment.Original => SoftwareBitmap.Copy(source),
            OcrImageTreatment.GrayscaleContrast => CreateGrayscaleContrastUpscaled(source),
            OcrImageTreatment.AdaptiveThreshold => CreateAdaptiveThresholdUpscaled(source),
            OcrImageTreatment.HighContrast => CreateHighContrastUpscaledLine(source),
            _ => throw new ArgumentOutOfRangeException(nameof(treatment))
        };

    /// <summary>Stretches grayscale contrast while retaining anti-aliased glyph edges.</summary>
    private static SoftwareBitmap CreateGrayscaleContrastUpscaled(SoftwareBitmap source)
    {
        using var bgra = source.BitmapPixelFormat == BitmapPixelFormat.Bgra8
            ? null
            : SoftwareBitmap.Convert(source, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var input = bgra ?? source;
        var width = input.PixelWidth;
        var height = input.PixelHeight;
        var scale = GetScale(width, height);
        var sourcePixels = ReadPixels(input);
        var outputWidth = checked((width + BorderPixels * 2) * scale);
        var outputHeight = checked((height + BorderPixels * 2) * scale);
        var outputPixels = new byte[checked(outputWidth * outputHeight * 4)];
        Array.Fill(outputPixels, (byte)255);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sourceIndex = ((y * width) + x) * 4;
                var luminance = ((sourcePixels[sourceIndex + 2] * 77)
                    + (sourcePixels[sourceIndex + 1] * 150)
                    + (sourcePixels[sourceIndex] * 29)) >> 8;
                var value = (byte)Math.Clamp(((luminance - 24) * 255) / 208, 0, 255);
                WriteScaledPixel(
                    outputPixels,
                    outputWidth,
                    (x + BorderPixels) * scale,
                    (y + BorderPixels) * scale,
                    scale,
                    value);
            }
        }

        return CreateBitmap(outputPixels, outputWidth, outputHeight);
    }

    /// <summary>
    /// Binarizes against a local background estimate, which can retain text over
    /// gradients, patterned dialogue boxes, and horizontal scanlines.
    /// </summary>
    private static SoftwareBitmap CreateAdaptiveThresholdUpscaled(SoftwareBitmap source)
    {
        using var bgra = source.BitmapPixelFormat == BitmapPixelFormat.Bgra8
            ? null
            : SoftwareBitmap.Convert(source, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var input = bgra ?? source;
        var width = input.PixelWidth;
        var height = input.PixelHeight;
        var scale = GetScale(width, height);
        var sourcePixels = ReadPixels(input);
        var integralStride = checked(width + 1);
        var integral = new long[checked(integralStride * (height + 1))];

        for (var y = 0; y < height; y++)
        {
            long rowSum = 0;
            for (var x = 0; x < width; x++)
            {
                var sourceIndex = ((y * width) + x) * 4;
                var luminance = ((sourcePixels[sourceIndex + 2] * 77)
                    + (sourcePixels[sourceIndex + 1] * 150)
                    + (sourcePixels[sourceIndex] * 29)) >> 8;
                rowSum += luminance;
                var integralIndex = ((y + 1) * integralStride) + x + 1;
                integral[integralIndex] = integral[(y * integralStride) + x + 1] + rowSum;
            }
        }

        var radius = Math.Clamp(Math.Min(width, height) / 8, 4, 18);
        var foreground = new byte[checked(width * height)];
        for (var y = 0; y < height; y++)
        {
            var top = Math.Max(0, y - radius);
            var bottom = Math.Min(height, y + radius + 1);
            for (var x = 0; x < width; x++)
            {
                var left = Math.Max(0, x - radius);
                var right = Math.Min(width, x + radius + 1);
                var localSum = integral[(bottom * integralStride) + right]
                    - integral[(top * integralStride) + right]
                    - integral[(bottom * integralStride) + left]
                    + integral[(top * integralStride) + left];
                var area = (right - left) * (bottom - top);
                var localThreshold = (localSum / area) + 8;

                var sourceIndex = ((y * width) + x) * 4;
                var luminance = ((sourcePixels[sourceIndex + 2] * 77)
                    + (sourcePixels[sourceIndex + 1] * 150)
                    + (sourcePixels[sourceIndex] * 29)) >> 8;
                foreground[(y * width) + x] = luminance >= localThreshold ? (byte)1 : (byte)0;
            }
        }

        // Repair isolated scanline breaks without widening every stroke.
        var repairedForeground = (byte[])foreground.Clone();
        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var index = (y * width) + x;
                if (foreground[index] == 0
                    && ((foreground[index - width] != 0 && foreground[index + width] != 0)
                        || (foreground[index - 1] != 0 && foreground[index + 1] != 0)))
                {
                    repairedForeground[index] = 1;
                }
            }
        }

        var outputWidth = checked((width + BorderPixels * 2) * scale);
        var outputHeight = checked((height + BorderPixels * 2) * scale);
        var outputPixels = new byte[checked(outputWidth * outputHeight * 4)];
        Array.Fill(outputPixels, (byte)255);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = repairedForeground[(y * width) + x] == 0 ? (byte)255 : (byte)0;
                WriteScaledPixel(
                    outputPixels,
                    outputWidth,
                    (x + BorderPixels) * scale,
                    (y + BorderPixels) * scale,
                    scale,
                    value);
            }
        }

        return CreateBitmap(outputPixels, outputWidth, outputHeight);
    }

    public static SoftwareBitmap CreateHighContrastUpscaledLine(SoftwareBitmap source)
    {
        using var bgra = source.BitmapPixelFormat == BitmapPixelFormat.Bgra8
            ? null
            : SoftwareBitmap.Convert(source, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var input = bgra ?? source;
        var width = input.PixelWidth;
        var height = input.PixelHeight;
        var scale = GetScale(width, height);
        var sourcePixels = ReadPixels(input);
        var threshold = FindOtsuThreshold(sourcePixels);
        var outputWidth = checked((width + BorderPixels * 2) * scale);
        var outputHeight = checked((height + BorderPixels * 2) * scale);
        var outputPixels = new byte[checked(outputWidth * outputHeight * 4)];
        Array.Fill(outputPixels, (byte)255);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sourceIndex = ((y * width) + x) * 4;
                var blue = sourcePixels[sourceIndex];
                var green = sourcePixels[sourceIndex + 1];
                var red = sourcePixels[sourceIndex + 2];
                var luminance = ((red * 77) + (green * 150) + (blue * 29)) >> 8;

                // The dialogue text is bright on a darker scene. Invert it so the
                // OCR engine receives dark glyphs on a clean light background.
                var value = luminance >= threshold ? (byte)0 : (byte)255;

                // A one-pixel dark scanline can break a bright vertical stroke.
                // Fill only isolated one-pixel gaps between matching dark pixels.
                if (value == 255 && y > 0 && y < height - 1
                    && IsForeground(sourcePixels, width, x, y - 1, threshold)
                    && IsForeground(sourcePixels, width, x, y + 1, threshold))
                {
                    value = 0;
                }

                WriteScaledPixel(
                    outputPixels,
                    outputWidth,
                    (x + BorderPixels) * scale,
                    (y + BorderPixels) * scale,
                    scale,
                    value);
            }
        }

        return CreateBitmap(outputPixels, outputWidth, outputHeight);
    }

    private static int GetScale(int width, int height)
    {
        var largestDimension = Math.Max(width, height) + (BorderPixels * 2);
        return Math.Clamp(MaximumOcrImageDimension / largestDimension, 1, MaximumScale);
    }

    private static void WriteScaledPixel(
        byte[] pixels,
        int imageWidth,
        int x,
        int y,
        int scale,
        byte value)
    {
        for (var scaleY = 0; scaleY < scale; scaleY++)
        {
            var rowOffset = ((y + scaleY) * imageWidth + x) * 4;
            for (var scaleX = 0; scaleX < scale; scaleX++)
            {
                var index = rowOffset + (scaleX * 4);
                pixels[index] = value;
                pixels[index + 1] = value;
                pixels[index + 2] = value;
                pixels[index + 3] = 255;
            }
        }
    }

    private static bool IsForeground(byte[] pixels, int width, int x, int y, int threshold)
    {
        var index = ((y * width) + x) * 4;
        var luminance = ((pixels[index + 2] * 77)
            + (pixels[index + 1] * 150)
            + (pixels[index] * 29)) >> 8;
        return luminance >= threshold;
    }

    private static int FindOtsuThreshold(byte[] pixels)
    {
        var histogram = new long[256];
        var pixelCount = pixels.Length / 4;
        long totalLuminance = 0;

        for (var index = 0; index < pixels.Length; index += 4)
        {
            var luminance = ((pixels[index + 2] * 77)
                + (pixels[index + 1] * 150)
                + (pixels[index] * 29)) >> 8;
            histogram[luminance]++;
            totalLuminance += luminance;
        }

        long backgroundCount = 0;
        long backgroundLuminance = 0;
        var bestVariance = double.MinValue;
        var threshold = 128;

        for (var value = 0; value < histogram.Length; value++)
        {
            backgroundCount += histogram[value];
            if (backgroundCount == 0)
            {
                continue;
            }

            var foregroundCount = pixelCount - backgroundCount;
            if (foregroundCount == 0)
            {
                break;
            }

            backgroundLuminance += value * histogram[value];
            var backgroundMean = (double)backgroundLuminance / backgroundCount;
            var foregroundMean = (double)(totalLuminance - backgroundLuminance) / foregroundCount;
            var meanDifference = backgroundMean - foregroundMean;
            var variance = (double)backgroundCount * foregroundCount * meanDifference * meanDifference;
            if (variance > bestVariance)
            {
                bestVariance = variance;
                threshold = value;
            }
        }

        return threshold;
    }

    private static byte[] ReadPixels(SoftwareBitmap bitmap)
    {
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var pixels = new byte[checked(width * height * 4)];

        using var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
        using var reference = buffer.CreateReference();
        reference.As<IMemoryBufferByteAccess>().GetBuffer(out var pointer, out _);
        var plane = buffer.GetPlaneDescription(0);
        var rowLength = checked(width * 4);
        if (plane.Stride == rowLength)
        {
            Marshal.Copy(IntPtr.Add(pointer, plane.StartIndex), pixels, 0, pixels.Length);
            return pixels;
        }
        for (var y = 0; y < height; y++)
        {
            Marshal.Copy(IntPtr.Add(pointer, plane.StartIndex + (y * plane.Stride)),
                pixels, y * rowLength, rowLength);
        }

        return pixels;
    }

    private static SoftwareBitmap CreateBitmap(byte[] pixels, int width, int height)
    {
        var bitmap = new SoftwareBitmap(
            BitmapPixelFormat.Bgra8,
            width,
            height,
            BitmapAlphaMode.Premultiplied);

        try
        {
            using var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Write);
            using var reference = buffer.CreateReference();
            reference.As<IMemoryBufferByteAccess>().GetBuffer(out var pointer, out _);
            var plane = buffer.GetPlaneDescription(0);
            var rowLength = checked(width * 4);
            if (plane.Stride == rowLength)
            {
                Marshal.Copy(pixels, 0, IntPtr.Add(pointer, plane.StartIndex), pixels.Length);
            }
            else
            {
                for (var y = 0; y < height; y++)
                    Marshal.Copy(pixels, y * rowLength,
                        IntPtr.Add(pointer, plane.StartIndex + (y * plane.Stride)), rowLength);
            }

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMemoryBufferByteAccess
    {
        void GetBuffer(out IntPtr buffer, out uint capacity);
    }
}
