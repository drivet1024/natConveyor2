export function browserId() {
    return document.cookie.split("; ")
        .find(cookie => cookie.startsWith("conveyor-browser="))?.split("=")[1] ?? "";
}
