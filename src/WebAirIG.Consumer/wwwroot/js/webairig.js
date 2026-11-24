// Tiny view helpers for Blazor JS interop (no application logic here).
window.webairig = {
    elementSize: function (el) {
        if (!el) return [];
        const r = el.getBoundingClientRect();
        return [r.width, r.height];
    }
};
