const NS = "http://www.w3.org/2000/svg";

export function trafficColor(count, maximum) {
    const ratio = maximum > 0 ? Math.max(0, Math.min(1, count / maximum)) : 0;
    return `rgb(${Math.round(255 - 10 * ratio)}, ${Math.round(224 - 94 * ratio)}, ${Math.round(102 - 70 * ratio)})`;
}

// Pure progression rule, shared with tests. Distance advances only while running.
export function advance(distance, seconds, running, unitsPerSecond) {
    return distance + (running ? Math.max(0, seconds) * unitsPerSecond : 0);
}

export function create(svg) {
    const layer = svg.querySelector('[data-parcel-layer]');
    const destinationLayer = svg.querySelector('[data-destination-layer]');
    const highlights = new Map();
    let traffic = new Map();
    let destinationsDirty = false;
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
        destinationsDirty = true;
    }
    function refreshDestinations() {
        if (!destinationsDirty) return;
        destinationsDirty = false;
        const counts = new Map();
        for (const parcel of parcels.values()) counts.set(parcel.chute, (counts.get(parcel.chute) ?? 0) + 1);
        const maximum = Math.max(0, ...[...traffic].filter(([chute]) => svg.querySelector(`#chute-${chute}-path`) || chute === 98).map(([, count]) => count));
        for (const [chute, highlight] of highlights) {
            if (!counts.has(chute) && !traffic.has(chute)) { highlight.group.remove(); highlights.delete(chute); }
        }
        for (const chute of new Set([...counts.keys(), ...traffic.keys()])) {
            const count = counts.get(chute) ?? 0;
            const recent = traffic.get(chute) ?? 0;
            let highlight = highlights.get(chute);
            if (!highlight) {
                const branch = svg.querySelector(chute === 98 ? '#recirculation-merge-path' : `#chute-${chute}-path`);
                if (!branch) continue;
                const group = document.createElementNS(NS, 'g');
                group.setAttribute('data-destination-chute', chute);
                const glow = document.createElementNS(NS, 'path');
                for (const [name, value] of Object.entries({d:branch.getAttribute('d'), fill:'none',
                    stroke:'#ffe066', 'stroke-width':26, 'stroke-opacity':.35, 'stroke-linecap':'round', 'stroke-linejoin':'round'}))
                    glow.setAttribute(name, value);
                const badge = document.createElementNS(NS, 'g');
                const length = branch.getTotalLength();
                const start = branch.getPointAtLength(0), direction = branch.getPointAtLength(Math.min(10,length));
                const position = branch.getPointAtLength(chute === 98 ? length / 2 : Math.min(direction.y > start.y ? 90 : 70,length));
                badge.setAttribute('transform', `translate(${position.x} ${position.y})`);
                const background = document.createElementNS(NS, 'rect');
                for (const [name,value] of Object.entries({x:-15,y:-11,width:30,height:22,rx:6,fill:'#a5ddff','fill-opacity':.85,stroke:'#d8f0ff'}))
                    background.setAttribute(name,value);
                const text = document.createElementNS(NS, 'text');
                for (const [name,value] of Object.entries({y:5,'text-anchor':'middle',fill:'#102b3b','font-family':'system-ui,sans-serif','font-size':13,'font-weight':700}))
                    text.setAttribute(name,value);
                badge.append(background,text);
                group.append(glow,badge);
                destinationLayer.append(group);
                highlight = {group,badge,text,glow,background};
                highlights.set(chute,highlight);
            }
            const color = trafficColor(recent, maximum);
            highlight.glow.setAttribute('stroke', color);
            highlight.glow.setAttribute('stroke-opacity', recent > 0 ? .45 : 0);
            highlight.background.setAttribute('fill', color);
            highlight.background.setAttribute('stroke', '#fff0b3');
            highlight.group.setAttribute('data-traffic-15-minutes', recent);
            highlight.group.setAttribute('data-en-route-count', count);
            highlight.badge.style.display = count > 1 ? '' : 'none';
            highlight.text.textContent = count;
        }
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
        refreshDestinations();
    }
    function animate(now) {
        frame = 0;
        if (disposed || !svg.isConnected) { dispose(); return; }
        tick(now);
        if (running && parcels.size) frame = requestAnimationFrame(animate);
    }
    function update(isRunning, station1, station2, traffic15Minutes = {}) {
        if (disposed) return;
        traffic = new Map(Object.entries(traffic15Minutes).map(([chute, count]) => [Number(chute), Number(count)]).filter(([, count]) => count > 0));
        destinationsDirty = true;
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
                    existing.chute = e.chute;
                    destinationsDirty = true;
                    existing.node.querySelector('title').textContent = `Destination ${e.chute} — position estimée`;
                    continue;
                }
                const node = document.createElementNS(NS, 'g');
                const box = document.createElementNS(NS, 'rect');
                for (const [attribute, value] of Object.entries({x:-5, y:-4, width:10, height:8, rx:1.5,
                    fill: lane === 0 ? '#ffcf78' : '#9edcff', stroke:'#fff', 'stroke-width':1}))
                    box.setAttribute(attribute, value);
                const title = document.createElementNS(NS, 'title');
                title.textContent = `Destination ${e.chute} — position estimée`;
                node.append(title, box);
                layer.append(node);
                parcels.set(key, { lane, chute: e.chute, node, route: itinerary, distance: 0 });
                destinationsDirty = true;
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
        for (const highlight of highlights.values()) highlight.group.remove();
        highlights.clear();
        paths.clear();
    }
    return { update, dispose };
}
