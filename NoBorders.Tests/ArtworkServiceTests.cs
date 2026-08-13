using NoBorders.Services;
using Xunit;

namespace NoBorders.Tests
{
    // review.md §2.3/§5: SanitizeFileName is internal specifically so this
    // regression test can reach it — confirms the path-traversal guard
    // added in that fix (an all-dots result can never reach Path.Combine)
    // survives future edits, plus the existing space/apostrophe/invalid-char
    // replacement behavior it always had.
    public class ArtworkServiceTests
    {
        [Fact]
        public void SanitizeFileName_ReplacesSpacesAndApostrophes()
        {
            Assert.Equal("Len_s_Island", ArtworkService.SanitizeFileName("Len's Island"));
        }

        [Theory]
        [InlineData("..")]
        [InlineData(".")]
        [InlineData("...")]
        public void SanitizeFileName_NeverReturnsAllDots(string input)
        {
            string result = ArtworkService.SanitizeFileName(input);

            Assert.False(result.Length > 0 && result.All(c => c == '.'),
                $"SanitizeFileName(\"{input}\") returned \"{result}\" — a pure-dots result could escape _cacheDir via Path.Combine.");
        }

        [Fact]
        public void SanitizeFileName_ReplacesPathSeparators()
        {
            string result = ArtworkService.SanitizeFileName(@"..\..\evil");
            Assert.DoesNotContain('\\', result);
            Assert.DoesNotContain('/', result);
        }

        [Fact]
        public void SanitizeFileName_LeavesOrdinaryNamesUnchanged()
        {
            Assert.Equal("HELLDIVERS2.exe", ArtworkService.SanitizeFileName("HELLDIVERS2.exe"));
        }
    }
}
