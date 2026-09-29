const NS = "http://www.w3.org/2000/svg";

// Pure progression rule, shared with tests. Distance advances only while running.
export function advance(distance, seconds, running, unitsPerSecond) {
    return distance + (running ? Math.max(0, seconds) * unitsPerSecond : 0);
}

export function create(svg) {
    const layer = svg.querySelector('[data-parcel-layer]');
    const paths = new Map();
    const parcels = new Map();
    const lanes = [{ sequence: null }, { sequence: null }];
    let running = false, disposed = false, frame = 0, last = performance.now();

    function path(d) {
        if (!paths.has(d)) {
            const element = document.createElementNS(NS, 'path');
            element.setAttribute('d', d);
            const length = element.getTotalLength();
            paths.set(d, { element, length });
        }
        return paths.get(d);
    }
    const geometry = id => svg.querySelector(`#${id}-path`).getAttribute('d');
    const sorter = geometry('merge-to-sorter');
    const returning = geometry('sorting-return');
    const recirculation = geometry('recirculation-merge');
    // Calibrate the complete upper loop, including the return from the merge to the scanner.
    const loop = path(`M480 510 H1330 ${geometry('merge-upper').replace(/^M1330 510/, '')} ${sorter.replace(/^M1420 535/, '')} ${returning.replace(/^M1540 300/, '')} ${recirculation.replace(/^M335 470/, '')}`);
    const unitsPerSecond = loop.length / (800 / (240 / 60));

    function route(lane, chute) {
        const y = lane === 0 ? 510 : 550;
        const merge = geometry(lane === 0 ? 'merge-upper' : 'merge-lower').replace(/^M1330 (510|550)/, '');
        const start = `M1248 ${y} H1330 ${merge} ${sorter.replace(/^M1420 535/, '')}`;
        if (chute === 98)
            return path(`${start} ${returning.replace(/^M1540 300/, '')} ${recirculation.replace(/^M335 470/, '')}`);
        const branch = svg.querySelector(`#chute-${chute}-path`);
        if (!branch) return null; // Unknown destinations must never be assigned to another chute.
        const d = branch.getAttribute('d');
        const match = /^M([\d.]+) 300\s*(.*)$/.exec(d);
        if (!match) return null;
        // Chute 16 is drawn as a bypass: retire the parcel within that branch, before its rejoin.
        return path(`${start} H${match[1]} ${chute === 16 ? 'C438 300 438 238 408 238 H305' : match[2]}`);
    }

    function remove(key) {
        parcels.get(key)?.node.remove();
        parcels.delete(key);
    }
    function tick(now) {
        const seconds = (now - last) / 1000;
        last = now;
        for (const [key, parcel] of parcels) {
            parcel.distance = advance(parcel.distance, seconds, running, unitsPerSecond);
            if (parcel.distance >= parcel.route.length) { remove(key); continue; }
            const p = parcel.route.element.getPointAtLength(parcel.distance);
            const next = parcel.route.element.getPointAtLength(Math.min(parcel.distance + 1, parcel.route.length));
            const angle = Math.atan2(next.y - p.y, next.x - p.x) * 180 / Math.PI;
            parcel.node.setAttribute('transform', `translate(${p.x} ${p.y}) rotate(${angle})`);
        }
    }
    function animate(now) {
        frame = 0;
        if (disposed || !svg.isConnected) { dispose(); return; }
        tick(now);
        if (running && parcels.size) frame = requestAnimationFrame(animate);
    }
    function update(isRunning, station1, station2) {
        if (disposed) return;
        tick(performance.now()); // Settle the previous running interval before changing state.
        running = isRunning;
        [station1, station2].forEach((events, lane) => {
            const state = lanes[lane];
            if (state.sequence === null) {
                state.sequence = events.reduce((max, e) => Math.max(max, e.sequence), 0);
                return; // Opening the page must not replay historical dispatches.
            }
            if (!events.length) {
                for (const [key, parcel] of parcels) if (parcel.lane === lane) remove(key);
                return;
            }
            for (const e of events) {
                if (e.sequence <= state.sequence) continue;
                state.sequence = e.sequence;
                const key = `${lane}:${e.parcelKey ?? e.sequence}`;
                const itinerary = route(lane, e.chute);
                if (!itinerary) { remove(key); continue; }
                const existing = parcels.get(key);
                if (existing) {
                    // A confirmed fallback write changes the same parcel's destination.
                    existing.route = itinerary;
                    existing.node.querySelector('title').textContent = `Destination ${e.chute} — position estimée`;
                    continue;
                }
                const node = document.createElementNS(NS, 'g');
                const box = document.createElementNS(NS, 'rect');
                for (const [attribute, value] of Object.entries({x:-8, y:-6, width:16, height:12, rx:2,
                    fill: lane === 0 ? '#ffcf78' : '#9edcff', stroke:'#fff', 'stroke-width':1.5}))
                    box.setAttribute(attribute, value);
                const title = document.createElementNS(NS, 'title');
                title.textContent = `Destination ${e.chute} — position estimée`;
                node.append(title, box);
                layer.append(node);
                parcels.set(key, { lane, node, route: itinerary, distance: 0 });
                // Bound memory even during a prolonged stop with continuing dispatches.
                if (parcels.size > 512) remove(parcels.keys().next().value);
            }
        });
        tick(performance.now());
        if (running && parcels.size && !frame) frame = requestAnimationFrame(animate);
    }
    const observer = new MutationObserver(() => { if (!svg.isConnected) dispose(); });
    observer.observe(document.body, {childList:true, subtree:true});
    function dispose() {
        if (disposed) return;
        disposed = true;
        cancelAnimationFrame(frame);
        observer.disconnect();
        for (const key of parcels.keys()) remove(key);
        paths.clear();
    }
    return { update, dispose };
}
