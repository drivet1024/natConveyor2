import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const source = await readFile(new URL('../Conveyor.Web/Components/Layout/ConveyorDiagram.razor.js', import.meta.url), 'utf8');
const { create, trafficColor } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

// Minimal SVG doubles: exercise highlight updates without a browser or a PLC.
class Element {
    attributes = new Map(); children = []; style = {};
    setAttribute(name, value) { this.attributes.set(name, String(value)); }
    getAttribute(name) { return this.attributes.get(name); }
    append(...children) { for (const child of children) { child.parent = this; this.children.push(child); } }
    remove() { this.parent.children = this.parent.children.filter(child => child !== this); }
    getTotalLength() { return 100; }
    getPointAtLength(distance) { return { x: 0, y: distance }; }
}

test('full chute overrides traffic, clears to current traffic and works without traffic', () => {
    globalThis.document = { createElementNS: () => new Element(), body: {} };
    globalThis.MutationObserver = class { observe() {} disconnect() {} };
    globalThis.cancelAnimationFrame = () => {};
    const destinations = new Element();
    const branch = new Element(); branch.setAttribute('d', 'M1152 300 V155');
    const svg = { isConnected: true, querySelector: selector =>
        selector === '[data-destination-layer]' ? destinations : branch };
    const animation = create(svg);
    const highlight = chute => destinations.children.find(child => child.getAttribute('data-destination-chute') === String(chute));
    try {
        animation.update(false, [], [], { 4: 10 });
        assert.equal(highlight(4).children[0].getAttribute('stroke'), trafficColor(10, 10));
        animation.update(false, [], [], { 4: 10 }, [4, 5]);
        for (const chute of [4, 5]) {
            assert.equal(highlight(chute).children[0].getAttribute('stroke'), '#ff4e5b');
            assert.equal(highlight(chute).children[0].getAttribute('stroke-opacity'), '0.9');
        }
        animation.update(false, [], [], { 4: 2, 6: 10 }, []);
        assert.equal(highlight(4).children[0].getAttribute('stroke'), trafficColor(2, 10));
        assert.equal(highlight(4).getAttribute('data-chute-full'), 'false');
        assert.equal(highlight(5), undefined);
    } finally { animation.dispose(); }
});
