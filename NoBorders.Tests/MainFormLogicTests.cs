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
        // Whole chains now strip, not just the last link (the bug the
        // previous version of this test documented as "observed, not
        // fixed") — CleanExeBaseName loops to a fixed point instead of a
        // single Regex.Replace pass.
        [InlineData("HELLDIVERS2-Win64-Shipping", "HELLDIVERS2")]
        [InlineData("HELLDIVERS2-WIN64-SHIPPING", "HELLDIVERS2")] // case-insensitive
        [InlineData("SomeGame-Debug", "SomeGame")]
        [InlineData("SomeGame-Test", "SomeGame")]
        [InlineData("SomeGame-D", "SomeGame")]
        [InlineData("SomeGame-Win32", "SomeGame")]
        [InlineData("PioneerGame", "PioneerGame")] // no tag — unchanged (this is ARC Raiders' real exe basename)
        // Leading tags now strip too, not just trailing ones.
        [InlineData("Win64-GameName", "GameName")]
        [InlineData("Win64-GameName-Shipping", "GameName")] // both ends in one call
        [InlineData("WinGDK-GameName-Test", "GameName")] // Xbox GDK variant
        // Safety cases: a known tag has to be a delimited token at an edge,
        // not just a substring, and "d" deliberately isn't in the prefix
        // list (see CleanExeBaseName's own doc comment for why).
        [InlineData("Testament", "Testament")] // "test" isn't delimited here
        [InlineData("Shipping-Game", "Game")] // "Shipping" now strips as a prefix
        [InlineData("D-Day", "D-Day")] // "d" as a prefix would mangle a real title
        public void CleanExeBaseName_StripsKnownBuildVariantTagsFromEitherEnd(string input, string expected)
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

        // Regression test modeled on a real corrupted entry found live in
        // games_config.json: a game's actual window title carried embedded
        // U+FEFF/U+200B/U+2005 characters that AddGame's old plain .Trim()
        // never touched, since they're not leading/trailing. \u escapes used
        // here (rather than pasting literal invisible characters) so the
        // test data is unambiguous in source instead of invisible in an editor.
        [Fact]
        public void SanitizeDisplayName_StripsEmbeddedZeroWidthAndBomCharacters()
        {
            string corrupted = "A﻿RC​ Rai​ders";
            Assert.Equal("ARC Raiders", MainForm.SanitizeDisplayName(corrupted));
        }

        [Theory]
        [InlineData("  Hades  ", "Hades")] // ordinary leading/trailing whitespace still trimmed
        [InlineData("Sea of Stars", "Sea of Stars")] // ordinary names pass through unchanged
        [InlineData("Foo Bar", "Foo Bar")] // non-breaking space normalized to a real space, not deleted
        public void SanitizeDisplayName_LeavesOrdinaryNamesIntact(string input, string expected)
        {
            Assert.Equal(expected, MainForm.SanitizeDisplayName(input));
        }
    }
}
