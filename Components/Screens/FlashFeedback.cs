using System;
using System.Threading.Tasks;

namespace NoBorders.Components.Screens
{
    /// <summary>
    /// Shared click-confirmation animation state, factored out of
    /// MatchingDetailPane's original Rescan implementation once the same
    /// effect was needed on several more "confirms or saves a setting"
    /// buttons across the app (user request) — Save Changes (Display +
    /// Matching tabs), Load Monitor Defaults, Save Monitor Default, and
    /// Detect Displays, alongside the original Rescan/Re-Apply. One
    /// instance per animated button, held as a field on the owning
    /// component and driven from that button's own @onclick handler via
    /// Trigger().
    ///
    /// See wwwroot/css/buttons.css's own doc comment for why CssClass must
    /// start empty and alternate between two distinctly-named keyframes
    /// rather than toggling one "on" class: a CSS animation only restarts
    /// when its computed animation-name actually changes, and starting
    /// from "idle" is what keeps a component mount/re-render from ever
    /// playing the animation on its own.
    /// </summary>
    public sealed class FlashFeedback
    {
        private readonly Action _requestRender;
        private bool _useA = true;

        public string CssClass { get; private set; } = "";

        public FlashFeedback(Action requestRender) => _requestRender = requestRender;

        public async Task Trigger()
        {
            CssClass = _useA ? "nb-feedback-flash-a" : "nb-feedback-flash-b";
            _useA = !_useA;
            _requestRender();

            await Task.Delay(1300);
            CssClass = "";
            _requestRender();
        }
    }
}
