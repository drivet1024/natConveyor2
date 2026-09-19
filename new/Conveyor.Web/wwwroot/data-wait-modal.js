export function show(dialog) {
    if (!dialog?.isConnected || dialog.open) return;
    dialog.addEventListener("cancel", event => event.preventDefault());
    dialog.showModal();
}
