using Xunit;

namespace NoBorders.Tests
{
    // review.md §3: GameConfig.IsMatch is the one real entry point every
    // enforcement call site in program.cs goes through — these tests exist
    // to make sure a future edit can't quietly break either MatchTarget
    // mode, especially the default (ProcessName), which every pre-existing
    // saved game relies on.
    public class GameConfigTests
    {
        private static GameConfig MakeGame(string pattern, MatchTargetMode mode = MatchTargetMode.ProcessName) =>
            new GameConfig { RegexPattern = pattern, MatchTarget = mode };

        [Fact]
        public void IsMatch_DefaultsToProcessName_TestsExeNameNotTitle()
        {
            var game = MakeGame(@"^helldivers2.*\.exe$");

            Assert.True(game.IsMatch("helldivers2.exe", "some unrelated window title"));
            Assert.False(game.IsMatch("notepad.exe", "HELLDIVERS 2"));
        }

        [Fact]
        public void IsMatch_WindowTitleMode_TestsTitleNotExeName()
        {
            var game = MakeGame(@"^HELLDIVERS.*2$", MatchTargetMode.WindowTitle);

            Assert.True(game.IsMatch("notepad.exe", "HELLDIVERS™ 2"));
            Assert.False(game.IsMatch("helldivers2.exe", "some unrelated window title"));
        }

        [Fact]
        public void IsMatch_WindowTitleMode_EmptyTitleDoesNotMatchARealPattern()
        {
            // A window that legitimately has no title (some background/tool
            // windows) shouldn't accidentally match a real, non-trivial
            // pattern just because the title is missing.
            var game = MakeGame(@"^HELLDIVERS.+2$", MatchTargetMode.WindowTitle);

            Assert.False(game.IsMatch("anything.exe", ""));
        }

        [Fact]
        public void MatchTarget_DefaultsToProcessName_ForBackwardCompatibility()
        {
            // Every pre-existing saved game deserializes without a MatchTarget
            // field at all (it didn't exist before review.md §3) — confirms
            // the property's own default keeps that path working unchanged.
            var game = new GameConfig();
            Assert.Equal(MatchTargetMode.ProcessName, game.MatchTarget);
        }
    }
}
