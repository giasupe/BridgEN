namespace BridgEN.App.Ocr;

/// <summary>
/// A pooled mask of horizontal glyph edges. Flat horizontal scanlines do not
/// produce horizontal edges; pooling also tolerates isolated broken pixels.
/// This is a visual change detector, not an OCR confidence estimate.
/// </summary>
internal sealed class OcrFrameSignature
{
    private readonly byte[] _features;
    private readonly int _imageWidth;
    private readonly int _imageHeight;
    private readonly int _columns;
    private readonly int _rows;
    private readonly bool _usesNeutralEdges;

    private OcrFrameSignature(byte[] features, int width, int height, int columns, int rows, bool usesNeutralEdges)
    {
        _features = features;
        _imageWidth = width;
        _imageHeight = height;
        _columns = columns;
        _rows = rows;
        _usesNeutralEdges = usesNeutralEdges;
    }

    public static OcrFrameSignature Create(ReadOnlySpan<byte> pixels, int width, int height)
    {
        if (width <= 0 || height <= 0 || pixels.Length != checked(width * height * 4))
        {
            throw new ArgumentException("Expected a contiguous BGRA image.");
        }

        var cellSize = Math.Max(2, (Math.Max(width, height) + 639) / 640);
        var columns = (width + cellSize - 1) / cellSize;
        var rows = (height + cellSize - 1) / cellSize;
        var neutralEdges = new byte[checked(columns * rows)];
        var allEdges = new byte[neutralEdges.Length];
        var luminance = new byte[checked(width * height)];
        for (var index = 0; index < luminance.Length; index++)
        {
            var pixel = index * 4;
            luminance[index] = (byte)((pixels[pixel + 2] * 77 + pixels[pixel + 1] * 150 + pixels[pixel] * 29) >> 8);
        }

        var radius = Math.Clamp(cellSize, 2, 6);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var center = luminance[y * width + x];
                var left = luminance[y * width + Math.Max(0, x - radius)];
                var right = luminance[y * width + Math.Min(width - 1, x + radius)];
                var contrast = Math.Max(Math.Abs(center - left), Math.Abs(center - right));
                if (contrast < 45)
                {
                    continue;
                }

                var feature = (y / cellSize) * columns + x / cellSize;
                allEdges[feature] = 1;
                var pixel = (y * width + x) * 4;
                var minimum = Math.Min(pixels[pixel], Math.Min(pixels[pixel + 1], pixels[pixel + 2]));
                var maximum = Math.Max(pixels[pixel], Math.Max(pixels[pixel + 1], pixels[pixel + 2]));
                if (maximum - minimum <= 64
                    && ((center >= 175 && center - Math.Min(left, right) >= 45)
                        || (center <= 80 && Math.Max(left, right) >= 170)))
                {
                    neutralEdges[feature] = 1;
                }
            }
        }

        // Prefer light/dark neutral glyphs over colored game scenery. Fall back
        // to all edges for colored fonts rather than treating their image as blank.
        var useNeutral = neutralEdges.Count(value => value != 0) >= 12;
        return new OcrFrameSignature(useNeutral ? neutralEdges : allEdges, width, height, columns, rows, useNeutral);
    }

    public bool HasSameSize(OcrFrameSignature other) =>
        _imageWidth == other._imageWidth && _imageHeight == other._imageHeight;

    public bool IsSameContent(OcrFrameSignature other)
    {
        if (_imageWidth != other._imageWidth || _imageHeight != other._imageHeight
            || _usesNeutralEdges != other._usesNeutralEdges)
        {
            return false;
        }

        var union = 0;
        var changed = 0;
        var commonTop = _rows;
        var commonBottom = -1;
        for (var index = 0; index < _features.Length; index++)
        {
            if ((_features[index] | other._features[index]) != 0) union++;
            if (_features[index] != other._features[index]) changed++;
            if ((_features[index] & other._features[index]) != 0)
            {
                commonTop = Math.Min(commonTop, index / _columns);
                commonBottom = Math.Max(commonBottom, index / _columns);
            }
        }

        if (changed == 0) return true;
        if (changed > Math.Max(6, union * 0.12)) return false;

        // A changed word can occupy only a small fraction of a long paragraph.
        // Look locally as well, while ignoring very small cursor/noise changes.
        if (changed < 3) return true;
        var smallChange = changed < Math.Max(6, union * 0.015);
        for (var top = 0; top < _rows; top += 4)
        {
            for (var left = 0; left < _columns; left += 8)
            {
                var localUnion = 0;
                var localChanged = 0;
                for (var y = top; y < Math.Min(top + 4, _rows); y++)
                {
                    // A tiny isolated indicator below/above the paragraph can
                    // blink. Local edits within the shared text rows still count.
                    if (smallChange && (y < commonTop - 1 || y > commonBottom + 1)) continue;
                    for (var x = left; x < Math.Min(left + 8, _columns); x++)
                    {
                        var index = y * _columns + x;
                        if ((_features[index] | other._features[index]) != 0) localUnion++;
                        if (_features[index] != other._features[index]) localChanged++;
                    }
                }

                if (localChanged >= 3 && localChanged > localUnion * 0.25) return false;
            }
        }

        return true;
    }
}
