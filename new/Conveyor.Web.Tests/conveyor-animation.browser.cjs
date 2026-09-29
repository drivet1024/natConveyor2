// Run with Node and Playwright installed (NODE_PATH can point to a temporary installation).
// Uses an isolated SVG fixture and synthetic confirmed dispatches; never connects to a PLC.
const { chromium } = require('playwright');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const assert = require('node:assert/strict');

(async () => {
    const component = path.resolve(__dirname, '../Conveyor.Web/Components/Layout/ConveyorDiagram.razor');
    const source = fs.readFileSync(component, 'utf8');
    const drawing = source.slice(source.indexOf('<defs>'), source.indexOf('<g id="chute-counters"'))
        .replace(/@key="[^"]*"/g, '').replace(/class="@\([^)]*\)"/g, '')
        .replace(/@ChuteTitle\((\d+)\)/g, 'Chute $1');
    const html = `<!doctype html><html lang="fr"><meta charset="utf-8"><title>Vérification animation</title>
        <body style="background:#10191e;color:#edf5f8;font-family:system-ui"><h2>Convoyeur du haut — positions estimées</h2>
        <p>Boucle ≈ 800 pieds · 240 pieds/minute · destination 98 : recirculation</p>
        <svg viewBox="0 0 1640 830">${drawing}<g data-parcel-layer="true"></g></svg>
        <script type="module">import {create} from '/animation.js'; window.animation = create(document.querySelector('svg'));</script>`;
    const server = http.createServer((req, res) => {
        res.setHeader('Content-Type', req.url === '/animation.js' ? 'text/javascript' : 'text/html; charset=utf-8');
        res.end(req.url === '/animation.js' ? fs.readFileSync(component + '.js') : html);
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    let browser;
    try {
        browser = await chromium.launch({channel: process.env.BROWSER_CHANNEL || 'msedge', headless:true});
        const page = await browser.newPage({viewport:{width:1640,height:1030}});
        const errors = [];
        page.on('pageerror', error => errors.push(error.message));
        await page.addInitScript(() => {
            let now = 0, id = 0;
            const callbacks = new Map();
            performance.now = () => now;
            window.requestAnimationFrame = callback => { callbacks.set(++id, callback); return id; };
            window.cancelAnimationFrame = key => callbacks.delete(key);
            window.step = seconds => {
                now += seconds * 1000;
                const pending = [...callbacks.values()]; callbacks.clear();
                for (const callback of pending) callback(now);
            };
        });
        await page.goto(`http://127.0.0.1:${server.address().port}`);
        await page.waitForFunction(() => !!window.animation);
        const result = await page.evaluate(() => {
            const layer = document.querySelector('[data-parcel-layer]');
            const count = () => layer.children.length;
            const destination = chute => document.querySelector(`[data-destination-chute="${chute}"]`);
            const check = (condition, message) => { if (!condition) throw new Error(message); };
            const event = (sequence, chute, parcelKey = String(sequence)) => ({sequence,chute,parcelKey});
            animation.update(true, [event(1, 1)], []);
            check(count() === 0, 'Historical values must not spawn parcels');
            check(!destination(1), 'Historical values must not highlight chutes');
            const burst = [event(2, 1), event(3, 98)];
            animation.update(true, burst, [event(1, 23)]);
            check(count() === 3, 'Both lanes and dispatch bursts must be preserved');
            check(destination(1)?.getAttribute('data-en-route-count') === '1', 'One parcel highlights destination');
            check(destination(1).querySelector('g').style.display === 'none', 'Single parcel has no numeric badge');
            check(layer.children[0].getAttribute('transform').startsWith('translate(1248 510)'), 'Station 1 origin');
            check(layer.children[2].getAttribute('transform').startsWith('translate(1248 550)'), 'Station 2 origin');
            step(10);
            animation.update(false, burst, [event(1,23)]);
            const before = layer.innerHTML;
            step(120);
            animation.update(false, burst, [event(1,23)]);
            check(before === layer.innerHTML, 'Stopped parcels must not move or duplicate');
            animation.update(true, [...burst,event(4,98,'2')], [event(1,23)]);
            check(count() === 3, 'Fallback must redirect the same parcel');
            check(!destination(1), 'Fallback removes the old destination highlight');
            check(destination(98)?.getAttribute('data-en-route-count') === '2', 'Recirculation counts all active parcels');
            check(destination(98).querySelector('g').style.display !== 'none', 'Multiple parcels display a badge');
            step(400);
            check(count() === 0, 'Parcels must disappear at their destination');
            check(!destination(98), 'Last arrival removes highlight and count');
            animation.update(true, [event(5,99)], []);
            check(count() === 0, 'Unknown destination must not be assigned a route');

            let sequence = 5;
            const chutes = [...Array(11)].map((_, i) => i+1).concat([13,16], [...Array(21)].map((_,i)=>i+21), [98]);
            const endpoints = [];
            for (const chute of chutes) {
                animation.update(true, [event(++sequence,chute)], []);
                let previous;
                for (let i=0; i<2400 && count(); i++) {
                    previous = layer.firstElementChild.getAttribute('transform');
                    step(.1);
                }
                check(count() === 0, `Chute ${chute} must terminate`);
                const [,x,y] = /translate\(([^ ]+) ([^)]+)\)/.exec(previous);
                const branch = chute === 98 ? document.querySelector('#recirculation-merge-path') : document.querySelector(`#chute-${chute}-path`);
                const expected = chute === 16 ? {x:305,y:238} : branch.getPointAtLength(branch.getTotalLength());
                check(Math.hypot(+x-expected.x,+y-expected.y)<3, `Wrong endpoint for ${chute}`);
                endpoints.push(chute);
            }
            animation.update(true, [event(++sequence,98)], []);
            animation.update(true, [], []);
            check(count() === 0, 'Counter reset clears active lane');
            animation.update(true, [event(++sequence,98)], []);
            check(count() === 1, 'New dispatch after reset must appear');
            animation.update(true, [event(++sequence,98)], [event(2,98)]);
            check(destination(98).getAttribute('data-en-route-count') === '3', 'Count combines both lanes');
            animation.update(false, [], [event(2,98)]);
            check(destination(98).getAttribute('data-en-route-count') === '1', 'Reset removes only its lane from destination count');
            check(destination(98).querySelector('g').style.display === 'none', 'Badge disappears when one parcel remains');
            animation.dispose();
            check(count() === 0, 'Disposal clears parcels');
            check(!destination(98), 'Disposal clears destination overlays');
            return {endpointsVerified:endpoints.length};
        });
        assert.deepEqual(errors, []);
        console.log(JSON.stringify(result));
        // Separate visual check, including the recirculation segment near the end of its route.
        await page.reload();
        await page.waitForFunction(() => !!window.animation);
        await page.evaluate(() => {
            animation.update(true, [], []);
            animation.update(true, [{sequence:1,chute:98}], [{sequence:1,chute:23}]);
            step(100);
            animation.update(true, [{sequence:1,chute:98},{sequence:2,chute:1},{sequence:3,chute:23}], [{sequence:1,chute:23},{sequence:2,chute:38},{sequence:3,chute:98}]);
        });
        const output = path.resolve(__dirname, '../../artifacts/conveyor-animation');
        fs.mkdirSync(output, {recursive:true});
        await page.screenshot({path:path.join(output,'animation.png'),fullPage:true});
    } finally {
        await browser?.close();
        await new Promise(resolve => server.close(resolve));
    }
})().catch(error => {console.error(error);process.exitCode=1;});
