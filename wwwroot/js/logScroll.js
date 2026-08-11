// Phase 6.4 (MIGRATION_PLAN.md): the Activity Log's Auto-Scroll behavior needs
// real scroll-position reads/writes that Blazor has no built-in way to do —
// the only JS interop anywhere in this app, kept to exactly this one need.
window.nbLogScroll = {
    toBottom: function (el) {
        if (el) el.scrollTop = el.scrollHeight;
    },
    // "Near" bottom (small tolerance) rather than exact, since a fractional
    // scrollHeight/zoom rounding difference shouldn't be read as "scrolled up".
    isNearBottom: function (el) {
        if (!el) return true;
        return el.scrollHeight - el.scrollTop - el.clientHeight < 24;
    }
};
