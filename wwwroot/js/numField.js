// User request (2026-08-13): clicking a SteppedNumberField's number turns it
// into a real <input> with the whole value pre-selected, ready to overtype —
// Blazor has no built-in way to select an input's text, so this is the one
// small JS interop call that needs (same reasoning as logScroll.js's own
// "the only JS interop anywhere in this app" — this is the second, equally
// narrow exception).
window.nbNumField = {
    selectAll: function (el) {
        if (!el) return;
        el.focus();
        el.select();
    }
};
