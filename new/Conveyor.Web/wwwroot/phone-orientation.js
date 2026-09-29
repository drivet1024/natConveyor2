// Screen orientation avoids treating the software keyboard as a phone rotation.
export function isPhoneLandscape(coarsePointer, width, height, orientationType) {
    return coarsePointer && Math.min(width, height) <= 768 &&
        (orientationType ? orientationType.startsWith('landscape') : width > height);
}

export function watch(receiver) {
    const pointer = matchMedia('(pointer: coarse)');
    const orientation = screen.orientation;
    let previous, disposed = false;
    function notify() {
        if (disposed) return;
        const landscape = isPhoneLandscape(pointer.matches, screen.width, screen.height, orientation?.type);
        if (landscape === previous) return;
        previous = landscape;
        receiver.invokeMethodAsync('SetPhoneLandscape', landscape).catch(() => {});
    }
    orientation?.addEventListener('change', notify);
    window.addEventListener('resize', notify);
    pointer.addEventListener('change', notify);
    notify();
    return { dispose() {
        disposed = true;
        orientation?.removeEventListener('change', notify);
        window.removeEventListener('resize', notify);
        pointer.removeEventListener('change', notify);
    }};
}
