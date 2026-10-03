using System.Diagnostics;
using AmbientLight.Core.Frames;
using AmbientLight.Core.Settings;
using AmbientLight.Core.Zones;
using AmbientLight.Processing.Letterbox;

namespace AmbientLight.Processing.Tests;

public sealed class LetterboxDetectorTests
{
    private const int Resolution = ZoneSampleFrame.ProfileResolution;
    private const float Picture = 0.5f;
    private const float Black = 0f;

    // 2.39:1 film on 16:9: bars of (1 - (16/9)/2.39) / 2 = 12.8% -> 32.8 of 256 lines.
    private const int CinemaBar = 33;

    private static readonly LetterboxParameters Parameters = LetterboxParameters.From(new LetterboxSettings(), Resolution);
    private static readonly long Second = Stopwatch.Frequency;

    [Fact]
    public void NoBars_KeepsFullScreen()
    {
        var detector = new LetterboxDetector();

        Assert.False(Feed(detector, Profile(0, 0), Profile(0, 0), 0));
        Assert.False(Feed(detector, Profile(0, 0), Profile(0, 0), 5 * Second));
        Assert.Equal(NormalizedRect.Full, detector.ContentBounds);
    }

    [Fact]
    public void CinemaBars_AreCropped_OnlyAfterTheyStayStable()
    {
        var detector = new LetterboxDetector();
        var rows = Profile(CinemaBar, CinemaBar);
        var columns = Profile(0, 0);

        Assert.False(Feed(detector, rows, columns, 0));
        Assert.False(Feed(detector, rows, columns, Second));          // 1.0 s < 1.5 s
        Assert.True(Feed(detector, rows, columns, 2 * Second));       // stable long enough

        Assert.Equal(CinemaBar, detector.VerticalBarLines);
        Assert.Equal(CinemaBar / (float)Resolution, detector.ContentBounds.Y, precision: 6);
        Assert.Equal(1f - (2f * CinemaBar / Resolution), detector.ContentBounds.Height, precision: 6);
        Assert.Equal(0f, detector.ContentBounds.X);
    }

    [Fact]
    public void SubtitlesInTheBottomBar_DoNotShrinkTheCrop()
    {
        var detector = Confirmed(CinemaBar, 0);

        // Subtitle text lights up rows 10..20 inside the bottom bar.
        var withSubtitles = Profile(CinemaBar, CinemaBar);
        for (var row = Resolution - 20; row < Resolution - 10; row++)
        {
            withSubtitles[row] = 0.9f;
        }

        Assert.False(Feed(detector, withSubtitles, Profile(0, 0), 10 * Second));
        Assert.Equal(CinemaBar, detector.VerticalBarLines);
    }

    [Fact]
    public void DarkSkyAtTheTopOnly_IsNotMistakenForABar()
    {
        var detector = new LetterboxDetector();
        var nightSky = Profile(60, 0);

        Feed(detector, nightSky, Profile(0, 0), 0);
        Assert.False(Feed(detector, nightSky, Profile(0, 0), 10 * Second));
        Assert.Equal(NormalizedRect.Full, detector.ContentBounds);
    }

    [Fact]
    public void PictureGrowingIntoBothBars_ShrinksTheCropImmediately()
    {
        var detector = Confirmed(CinemaBar, 0);

        // Aspect ratio changes to 16:9 (e.g. IMAX scene): no bars at all.
        Assert.True(Feed(detector, Profile(0, 0), Profile(0, 0), 20 * Second + 1));

        Assert.Equal(0, detector.VerticalBarLines);
        Assert.Equal(NormalizedRect.Full, detector.ContentBounds);
    }

    [Fact]
    public void JitterWithinTolerance_ChangesNothing()
    {
        var detector = Confirmed(CinemaBar, 0);

        Assert.False(Feed(detector, Profile(CinemaBar - 1, CinemaBar + 1), Profile(0, 0), 30 * Second));
        Assert.False(Feed(detector, Profile(CinemaBar + 2, CinemaBar + 2), Profile(0, 0), 40 * Second));
        Assert.Equal(CinemaBar, detector.VerticalBarLines);
    }

    [Fact]
    public void FullyBlackFrame_CarriesNoInformation()
    {
        var detector = Confirmed(CinemaBar, 0);

        Assert.False(Feed(detector, Profile(Resolution, 0), Profile(Resolution, 0), 30 * Second));
        Assert.Equal(CinemaBar, detector.VerticalBarLines);
    }

    [Fact]
    public void BarsLargerThanTheMaximum_AreTreatedAsADarkScene()
    {
        var detector = new LetterboxDetector();
        var huge = Profile(100, 100); // 39% per side, above the 30% default

        Feed(detector, huge, Profile(0, 0), 0);
        Assert.False(Feed(detector, huge, Profile(0, 0), 10 * Second));
        Assert.Equal(NormalizedRect.Full, detector.ContentBounds);
    }

    [Fact]
    public void Pillarbox_4By3OnWidescreen_CropsLeftAndRight()
    {
        // 4:3 on 16:9: (1 - 0.75) / 2 = 12.5% each side -> 32 of 256 columns.
        var detector = new LetterboxDetector();
        var columns = Profile(32, 32);

        Feed(detector, Profile(0, 0), columns, 0);
        Assert.True(Feed(detector, Profile(0, 0), columns, 2 * Second));

        Assert.Equal(32, detector.HorizontalBarLines);
        Assert.Equal(0.125f, detector.ContentBounds.X, precision: 6);
        Assert.Equal(0.75f, detector.ContentBounds.Width, precision: 6);
        Assert.Equal(1f, detector.ContentBounds.Height);
    }

    [Fact]
    public void ChangingBarsDuringTheStabilityWindow_RestartsTheTimer()
    {
        var detector = new LetterboxDetector();

        Feed(detector, Profile(20, 20), Profile(0, 0), 0);
        Feed(detector, Profile(CinemaBar, CinemaBar), Profile(0, 0), Second);   // new candidate at 1 s
        Assert.False(Feed(detector, Profile(CinemaBar, CinemaBar), Profile(0, 0), 2 * Second)); // only 1 s stable
        Assert.True(Feed(detector, Profile(CinemaBar, CinemaBar), Profile(0, 0), 3 * Second));
        Assert.Equal(CinemaBar, detector.VerticalBarLines);
    }

    [Fact]
    public void Reset_ReturnsToFullScreen()
    {
        var detector = Confirmed(CinemaBar, 0);

        Assert.True(detector.Reset());
        Assert.Equal(NormalizedRect.Full, detector.ContentBounds);
        Assert.False(detector.Reset());
    }

    [Fact]
    public void BlackRuns_AreCountedFromBothEnds()
    {
        float[] profile = [0f, 0f, 0.5f, 0f, 0.3f, 0f, 0f, 0f];

        Assert.Equal(2, LetterboxDetector.CountLeadingBlack(profile, 0.001f));
        Assert.Equal(3, LetterboxDetector.CountTrailingBlack(profile, 0.001f));
    }

    [Fact]
    public void Parameters_ConvertSettingsToDetectorUnits()
    {
        var parameters = LetterboxParameters.From(new LetterboxSettings { BlackLevel = 12, StableTimeMs = 1500, MaxBarFraction = 0.3f }, Resolution);

        Assert.Equal(0.0036765f, parameters.BlackLevelLinear, precision: 5);  // sRGB 12/255 in linear light
        Assert.Equal(Stopwatch.Frequency * 3 / 2, parameters.StableTicks);
        Assert.Equal(76, parameters.MaxBarLines);
    }

    private static LetterboxDetector Confirmed(int verticalBar, int horizontalBar)
    {
        var detector = new LetterboxDetector();
        Feed(detector, Profile(verticalBar, verticalBar), Profile(horizontalBar, horizontalBar), 0);
        Feed(detector, Profile(verticalBar, verticalBar), Profile(horizontalBar, horizontalBar), 2 * Second);
        Assert.Equal(verticalBar, detector.VerticalBarLines);
        return detector;
    }

    private static bool Feed(LetterboxDetector detector, float[] rows, float[] columns, long timestamp) =>
        detector.Update(rows, columns, timestamp, Parameters);

    private static float[] Profile(int leadingBlack, int trailingBlack)
    {
        var profile = new float[Resolution];
        for (var i = 0; i < Resolution; i++)
        {
            profile[i] = i < leadingBlack || i >= Resolution - trailingBlack ? Black : Picture;
        }

        return profile;
    }
}
