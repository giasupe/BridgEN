using speciRPG.App.Ocr;

const string complete = "Rehabilitation training plan:\nAfter the subject is awake, please be sure\nto instruct the subject to complete\nthe rehabilitation training under surveillance";
const string damaged = "Rehabilitation-training plan\nAfter the subject-is awake,please besure\nto instructthe-subjeet@gemblete\nthe undersurveillance";
var tests = new (string Name, Action Run)[]
{
    ("Column-major OCR instructions reconstruct into their three visual rows", () =>
    {
        var words = new OcrWordBox[]
        {
            new("Hold", 36, 20, 90, 32), new("Hold", 36, 80, 90, 32), new("Tap", 36, 140, 75, 32),
            new("[Arrow", 154, 18, 120, 36), new("keys]", 290, 20, 100, 32), new("to", 410, 26, 30, 24), new("move", 454, 26, 100, 24),
            new("[Shift]", 154, 78, 130, 36), new("to", 380, 86, 30, 24), new("run", 430, 86, 60, 24),
            new("[Z/Space]", 154, 138, 180, 36), new("to", 420, 146, 30, 24), new("Investigate", 465, 140, 180, 32), new("or", 660, 146, 30, 24), new("talk", 705, 140, 70, 32)
        };
        var expected = new[] { "Hold [Arrow keys] to move", "Hold [Shift] to run", "Tap [Z/Space] to Investigate or talk" };
        Check(OcrTextLayout.BuildLines(words).SequenceEqual(expected), "OCR columns were left in block order.");
        Check(OcrTextLayout.BuildLines(words.Reverse()).SequenceEqual(expected), "Input block order changed the visual rows.");
        Check(OcrTextLayout.BuildLines(words.Select(word => word with { X = word.X * 2, Y = word.Y * 2, Width = word.Width * 2, Height = word.Height * 2 })).SequenceEqual(expected), "Upscaling changed the reconstructed rows.");
    }),
    ("Line reconstruction supports arbitrary row counts and nearby rows", () =>
    {
        Check(OcrTextLayout.BuildLines([]).Count == 0, "An empty image generated a line.");
        for (var count = 1; count <= 6; count++)
        {
            var words = Enumerable.Range(0, count).SelectMany(row => new[]
            {
                new OcrWordBox("Line", 10, row * 25, 40, 20),
                new OcrWordBox((row + 1).ToString(), 70, row * 25 + 2, 12, 16)
            });
            var lines = OcrTextLayout.BuildLines(words.Reverse());
            Check(lines.SequenceEqual(Enumerable.Range(1, count).Select(number => $"Line {number}")), "Adjacent rows merged or a fixed line count was assumed.");
        }
    }),
    ("Split brackets and slash retain literal keyboard instructions", () =>
    {
        var words = new[] { "Tap", "[", "Z", "/", "Space", "]", "to", "talk", "." }
            .Select((text, index) => new OcrWordBox(text, index * 40, 10, 30, 20));
        Check(OcrTextLayout.BuildLines(words).Single() == "Tap [Z/Space] to talk.", "Keyboard punctuation was separated from its label.");
        Check(OcrTextQuality.Score("Hold [Shift] to run") > OcrTextQuality.Score("Hold to run"), "Dropping a keyboard label improved its quality score.");
    }),
    ("Invalid OCR boxes do not contaminate row positions", () =>
    {
        var words = new[] { new OcrWordBox("Valid", 10, 10, 40, 20), new OcrWordBox("noise", double.NaN, 0, 1, 1), new OcrWordBox("noise", 0, 0, 0, 10) };
        Check(OcrTextLayout.BuildLines(words).Single() == "Valid", "Unusable geometry produced a spurious row.");
    }),
    ("Complete rehabilitation passage outranks dropped/garbled words", () =>
    {
        Check(OcrTextQuality.IsBetterCandidate(complete, damaged), "Complete text must win.");
        Check(!OcrTextQuality.IsBetterCandidate(damaged, complete), "Damaged text must not win.");
        Check(!OcrTextQuality.ShouldRetry("rehabilitation training under surveillance"), "Valid long words must not trigger retries.");
        Check(OcrTextQuality.Score("rehabilitation") > 0, "Deleting a valid long word must not improve its score.");
    }),
    ("Alternating raw and enhanced readings do not reset a stable passage", () =>
    {
        var stabilizer = new OcrTextStabilizer();
        var frame = Frame(complete);
        var first = stabilizer.Observe(frame, complete, Seconds(0));
        Check(!first.IsStable, "One frame cannot establish stability.");
        for (var pass = 1; pass <= 10; pass++)
        {
            var result = stabilizer.Observe(frame, pass % 2 == 0 ? complete : damaged, Seconds(pass * 0.8));
            Check(result.Text == first.Text && result.Revision == first.Revision, "A poorer OCR pass replaced the accepted text.");
            Check(result.IsStable, "Repeated unchanged source glyphs must confirm stability.");
        }
    }),
    ("A recovered passage is retained when the next raw pass loses words", () =>
    {
        var stabilizer = new OcrTextStabilizer();
        var frame = Frame(complete);
        stabilizer.Observe(frame, damaged, Seconds(0));
        var recovered = stabilizer.Observe(frame, complete, Seconds(0.8));
        Check(recovered.Text == OcrTextQuality.Normalize(complete) && !recovered.IsStable, "An improved reading needs confirmation.");
        var next = stabilizer.Observe(frame, damaged, Seconds(1.6));
        Check(next.Text == recovered.Text && next.IsStable, "Recovery was undone by a raw pass.");
        Check(next.UnchangedFor == Seconds(0.8), "Recovery must restart the one-second stability period.");
    }),
    ("Moving bright scenery does not reset an unchanged passage", () =>
    {
        var stabilizer = new OcrTextStabilizer();
        var first = stabilizer.Observe(Frame(complete, backgroundPhase: 0), complete, Seconds(0));
        for (var pass = 1; pass < 8; pass++)
        {
            var result = stabilizer.Observe(Frame(complete, backgroundPhase: pass % 3), complete, Seconds(pass * 0.8));
            Check(result.Revision == first.Revision && result.IsStable, "Scenery alone restarted translation stability.");
        }
    }),
    ("Reported good/bad OCR alternation settles even while scenery moves", () =>
    {
        var stabilizer = new OcrTextStabilizer();
        var first = stabilizer.Observe(Frame(complete, backgroundPhase: 0), complete, Seconds(0));
        for (var pass = 1; pass < 10; pass++)
        {
            var result = stabilizer.Observe(Frame(complete, backgroundPhase: pass % 3), pass % 2 == 0 ? complete : damaged, Seconds(pass * 0.8));
            Check(result.Text == first.Text && result.Revision == first.Revision && result.IsStable, "Visual noise defeated candidate retention.");
        }
    }),
    ("A new visually different dialogue replaces a higher-scoring old passage", () =>
    {
        var stabilizer = new OcrTextStabilizer();
        var oldFrame = Frame(complete);
        stabilizer.Observe(oldFrame, complete, Seconds(0));
        var old = stabilizer.Observe(oldFrame, damaged, Seconds(0.8));
        const string newText = "Stop. Do not enter.";
        var nextFrame = Frame(newText);
        Check(stabilizer.HasContentChanged(nextFrame, newText), "New dialogue must invalidate pending translation before recovery OCR.");
        var next = stabilizer.Observe(nextFrame, newText, Seconds(1.6));
        Check(next.Text == newText && next.Revision != old.Revision && !next.IsStable, "An old high score kept the previous dialogue visible.");
        Check(stabilizer.Observe(nextFrame, newText, Seconds(2.4)).IsStable, "New dialogue was never confirmed.");
    }),
    ("A local word change is detected within a long paragraph", () =>
    {
        var first = Frame(complete);
        var second = Frame(complete.Replace("awake", "asleep"));
        Check(!first.IsSameContent(second), "A localized word change was mistaken for OCR jitter.");
    }),
    ("A one-letter edit is detected without relying on a different line length", () =>
    {
        Check(!Frame(complete).IsSameContent(Frame(complete.Replace("awake", "aware"))), "A one-letter change remained pinned to the previous reading.");
    }),
    ("A repeated small edit can replace the old passage even with a lower score", () =>
    {
        var stabilizer = new OcrTextStabilizer();
        var old = stabilizer.Observe(Frame(complete), complete, Seconds(0));
        var changed = complete.Replace("awake", "aware");
        var source = Frame(changed);
        var first = stabilizer.Observe(source, changed, Seconds(0.8));
        Check(first.Revision == old.Revision, "A single similar OCR variant should not replace the passage.");
        Check(stabilizer.HasContentChanged(source, changed), "A repeated small edit should be checked before recovery.");
        var confirmed = stabilizer.Observe(source, changed, Seconds(1.6));
        Check(confirmed.Revision != old.Revision && confirmed.Text == OcrTextQuality.Normalize(changed), "The old passage remained pinned after corroboration.");
        Check(confirmed.UnchangedFor == Seconds(0.8), "The first supporting observation must count toward stability time.");
    }),
    ("Background motion cannot cancel translation when the OCR text is unchanged", () =>
    {
        var stabilizer = new OcrTextStabilizer();
        stabilizer.Observe(Frame(complete, backgroundPhase: 0), complete, Seconds(0));
        Check(!stabilizer.HasContentChanged(Frame(complete, backgroundPhase: 1), complete), "Pixel-only invalidation would keep canceling Gemini.");
    }),
    ("Punctuation variations do not turn yes into no or discard numeric edits", () =>
    {
        Check(OcrTextSimilarity.Key("After the subject-is awake.") == OcrTextSimilarity.Key("After the subject is awake"), "Comparison should tolerate OCR punctuation.");
        Check(!OcrTextSimilarity.IsSamePassage("yes", "no"), "Short opposite responses must be different passages.");
        Check(OcrTextSimilarity.Key("HP: 100") != OcrTextSimilarity.Key("HP: 200"), "Numeric content was lost by normalization.");
    }),
    ("A punctuation-only RPG dialogue is not treated as an empty reading", () =>
    {
        var stabilizer = new OcrTextStabilizer();
        var frame = Frame("...");
        stabilizer.Observe(frame, "...", Seconds(0));
        var next = stabilizer.Observe(frame, "...", Seconds(0.8));
        Check(next.Text == "..." && next.IsStable, "An ellipsis-only dialogue disappeared.");
    }),
    ("A small isolated blinking indicator does not invalidate a paragraph", () =>
    {
        Check(Frame(complete).IsSameContent(Frame(complete, indicator: true)), "A blinking indicator reset visual confirmation.");
    }),
    ("Typewriter growth cannot accumulate indefinitely under the same anchor", () =>
    {
        var stabilizer = new OcrTextStabilizer();
        var first = stabilizer.Observe(Frame("After the"), "After the", Seconds(0));
        var next = stabilizer.Observe(Frame("After the subject is awake"), "After the subject is awake", Seconds(0.8));
        Check(next.Revision != first.Revision && !next.IsStable, "Growing text was treated as complete.");
    }),
    ("Whitespace changes do not create a new accepted reading", () =>
    {
        var stabilizer = new OcrTextStabilizer();
        var frame = Frame("After the subject is awake");
        var first = stabilizer.Observe(frame, "After the subject is awake", Seconds(0));
        var next = stabilizer.Observe(frame, "  After  the subject   is awake  ", Seconds(0.8));
        Check(next.Revision == first.Revision && next.IsStable, "Whitespace reset stability.");
    }),
    ("One empty reading holds the caption but blocks translation; two clear it", () =>
    {
        var stabilizer = new OcrTextStabilizer();
        var frame = Frame(complete);
        stabilizer.Observe(frame, complete, Seconds(0));
        var first = stabilizer.Observe(frame, string.Empty, Seconds(0.8));
        Check(first.Text.Length > 0 && !first.IsStable, "A transient empty result must hold the caption but invalidate translation.");
        var second = stabilizer.Observe(frame, string.Empty, Seconds(1.6));
        Check(second.Text.Length == 0 && !second.IsStable, "Repeated empty results must clear the caption.");
        var recovered = stabilizer.Observe(frame, complete, Seconds(2.4));
        Check(!recovered.IsStable && recovered.UnchangedFor == TimeSpan.Zero, "Recovery after an empty read needs fresh confirmation.");
    }),
    ("A cleared source image cannot retain the previous dialogue", () =>
    {
        var stabilizer = new OcrTextStabilizer();
        stabilizer.Observe(Frame(complete), complete, Seconds(0));
        var blank = Frame("");
        var first = stabilizer.Observe(blank, "", Seconds(0.8));
        Check(!first.IsStable, "A blank source must immediately invalidate translation.");
        Check(stabilizer.Observe(blank, "", Seconds(1.6)).Text.Length == 0, "The old caption remained after the source cleared.");
    }),
    ("Horizontal scanline gaps and dark colored backgrounds tolerate pooling", () =>
    {
        var first = Frame(complete);
        Check(first.IsSameContent(Frame(complete, scanlines: true)), "One-pixel horizontal gaps reset the scene.");
        Check(first.IsSameContent(Frame(complete, coloredBackground: true)), "Dark colored background changes reset stable glyphs.");
    }),
    ("Changing capture dimensions resets visual confirmation", () =>
    {
        Check(!Frame(complete).IsSameContent(Frame(complete, width: 1100)), "Resizing must invalidate visual confirmation.");
    })
};

var failures = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception exception) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}"); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} passed");
return failures == 0 ? 0 : 1;

static TimeSpan Seconds(double seconds) => TimeSpan.FromSeconds(seconds);
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

// Synthetic glyph images isolate the scene detector from Windows OCR. The OCR
// readings above are the user's actual failure examples, not outputs of this font.
static OcrFrameSignature Frame(string text, bool scanlines = false, bool coloredBackground = false, int width = 1000, bool indicator = false, int backgroundPhase = -1)
{
    const int height = 180;
    var pixels = new byte[width * height * 4];
    for (var index = 0; index < width * height; index++)
    {
        pixels[index * 4] = coloredBackground ? (byte)70 : (byte)30;
        pixels[index * 4 + 1] = coloredBackground ? (byte)25 : (byte)30;
        pixels[index * 4 + 2] = coloredBackground ? (byte)45 : (byte)30;
        pixels[index * 4 + 3] = 255;
    }
    if (backgroundPhase >= 0)
    {
        for (var row = 0; row < height; row++)
        for (var column = 780; column < width; column++)
        {
            var value = ((column + backgroundPhase * 7) / 13) % 2 == 0 ? (byte)205 : (byte)50;
            var offset = (row * width + column) * 4;
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = value;
        }
    }
    var x = 16;
    var y = 16;
    foreach (var character in text)
    {
        if (character == '\n') { x = 16; y += 32; continue; }
        if (character != ' ')
        {
            for (var row = 0; row < 7; row++)
            {
                var bits = (uint)(character * 4051 + row * 1999);
                for (var column = 0; column < 5; column++)
                {
                    if (((bits >> column) & 1) == 0) continue;
                    for (var dy = 0; dy < 2; dy++)
                    for (var dx = 0; dx < 2; dx++)
                    {
                        var px = x + column * 2 + dx;
                        var py = y + row * 2 + dy;
                        if (px >= width || py >= height || (scanlines && py % 4 == 0)) continue;
                        var offset = (py * width + px) * 4;
                        pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 240;
                    }
                }
            }
        }
        x += 14;
    }
    if (indicator)
    {
        for (var row = 0; row < 5; row++)
        for (var column = row; column < 10 - row; column++)
        {
            var offset = ((height - 10 + row) * width + width / 2 + column) * 4;
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 240;
        }
    }
    return OcrFrameSignature.Create(pixels, width, height);
}
