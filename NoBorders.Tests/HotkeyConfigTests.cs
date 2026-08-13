using System.Windows.Forms;
using NoBorders;
using Xunit;

namespace NoBorders.Tests
{
    // review.md §5: HotkeyConfig.ToString() is what every hotkey status
    // label/capture display actually shows the user — worth pinning down
    // directly rather than only ever eyeballing it live.
    public class HotkeyConfigTests
    {
        [Fact]
        public void ToString_NoKey_ReturnsNone()
        {
            var config = new HotkeyConfig { Modifiers = 0, Key = 0 };
            Assert.Equal("None", config.ToString());
        }

        [Fact]
        public void ToString_BareKey_NoModifiers()
        {
            var config = new HotkeyConfig { Modifiers = 0, Key = (uint)Keys.F9 };
            Assert.Equal("F9", config.ToString());
        }

        [Fact]
        public void ToString_CtrlShift_MatchesRegisteredOrder()
        {
            // MOD_CONTROL (0x0002) | MOD_SHIFT (0x0004) — the app's own default
            // "Add focused app" binding (Ctrl+Shift+A).
            var config = new HotkeyConfig { Modifiers = 0x0002 | 0x0004, Key = (uint)Keys.A };
            Assert.Equal("Ctrl + Shift + A", config.ToString());
        }

        [Fact]
        public void ToString_MasksOutModNoRepeat()
        {
            // MOD_NOREPEAT (0x4000) is always set internally before
            // RegisterHotKey but must never leak into the display text — it's
            // an implementation detail, not something the user chose.
            var config = new HotkeyConfig { Modifiers = 0x0002 | 0x4000, Key = (uint)Keys.F10 };
            Assert.Equal("Ctrl + F10", config.ToString());
        }

        [Fact]
        public void ToString_Alt_IncludedInOrder()
        {
            var config = new HotkeyConfig { Modifiers = 0x0002 | 0x0004 | 0x0001, Key = (uint)Keys.R };
            Assert.Equal("Ctrl + Shift + Alt + R", config.ToString());
        }
    }
}
