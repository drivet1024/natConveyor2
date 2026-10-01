export function watch(reference) {
    const onKeyDown = event => {
        if (event.key !== "Escape" || !document.querySelector(".chute-full-backdrop")) return;
        event.preventDefault();
        event.stopImmediatePropagation();
        reference.invokeMethodAsync("DismissChuteAlarmFromKeyboard").catch(() => {});
    };
    window.addEventListener("keydown", onKeyDown, true);
    return {
        dispose() { window.removeEventListener("keydown", onKeyDown, true); }
    };
}
