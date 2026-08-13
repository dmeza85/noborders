using NoBorders;
using Xunit;

namespace NoBorders.Tests
{
    // review.md §5: "even a small xunit project covering the pure-function
    // pieces ... would catch regressions for free going forward." Both
    // methods under test are internal statics extracted from MainForm
    // specifically to make this possible (program.cs's own doc comments on
    // them explain why) — neither touches any WinForms control or instance
    // state, so no Form ever needs to be constructed here.
    public class MainFormLogicTests
    {
        [Theory]
        // Only the last suffix in a chain is stripped per pass — Regex.Replace
        // doesn't re-scan the shortened string for a second match, so
        // "-Win64-Shipping" (a common Unreal Engine naming pattern) only loses
        // "-Shipping" here. Documented as observed, not "fixed": the leftover
        // "-Win64" still ends up inside the pattern's ".*" wildcard, so the
        // built match pattern still matches the real exe correctly end to
        // end — this only affects the cosmetic fallback display name for
        // games added via Browse-for-EXE with no running window to name them
        // from. Out of scope for review.md §5 (which asked for tests of
        // existing behavior, not a behavior change) — noted here for whoever
        // picks it up next.
        [InlineData("HELLDIVERS2-Win64-Shipping", "HELLDIVERS2-Win64")]
        [InlineData("HELLDIVERS2-WIN64-SHIPPING", "HELLDIVERS2-WIN64")] // case-insensitive
        [InlineData("SomeGame-Debug", "SomeGame")]
        [InlineData("SomeGame-Test", "SomeGame")]
        [InlineData("SomeGame-D", "SomeGame")]
        [InlineData("SomeGame-Win32", "SomeGame")]
        [InlineData("PioneerGame", "PioneerGame")] // no suffix — unchanged
        [InlineData("Shipping-Game", "Shipping-Game")] // suffix must be at the end
        public void CleanExeBaseName_StripsKnownBuildVariantSuffixes(string input, string expected)
        {
            Assert.Equal(expected, MainForm.CleanExeBaseName(input));
        }

        [Theory]
        [InlineData(MonitorAlignMode.Left,   3440, 1440, 2560, 1440, 0,    0)]
        [InlineData(MonitorAlignMode.Right,  3440, 1440, 2560, 1440, 880,  0)]
        [InlineData(MonitorAlignMode.Bottom, 3440, 1440, 2560, 1080, 440,  360)]
        [InlineData(MonitorAlignMode.Center, 3440, 1440, 2560, 1440, 440,  0)]
        public void ComputeAlignOffset_MatchesEachAlignMode(
            MonitorAlignMode mode, int monitorWidth, int monitorHeight, int windowWidth, int windowHeight,
            int expectedOffsetX, int expectedOffsetY)
        {
            var (offsetX, offsetY) = MainForm.ComputeAlignOffset(monitorWidth, monitorHeight, windowWidth, windowHeight, mode);

            Assert.Equal(expectedOffsetX, offsetX);
            Assert.Equal(expectedOffsetY, offsetY);
        }

        [Fact]
        public void ComputeAlignOffset_FullCoverage_CentersToZero()
        {
            // Window exactly matches monitor size — every mode should agree
            // Offset is 0,0 (full coverage), matching the "fits on screen"
            // full-coverage case ResultPreview.razor's caption describes.
            var (offsetX, offsetY) = MainForm.ComputeAlignOffset(1920, 1080, 1920, 1080, MonitorAlignMode.Center);
            Assert.Equal(0, offsetX);
            Assert.Equal(0, offsetY);
        }
    }
}
