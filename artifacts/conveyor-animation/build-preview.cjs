const fs = require('node:fs');
const path = require('node:path');
const sourcePath = path.resolve(__dirname, '../../new/Conveyor.Web/Components/Layout/ConveyorDiagram.razor');
const source = fs.readFileSync(sourcePath, 'utf8');
const drawing = source.slice(source.indexOf('<defs>'), source.indexOf('<g id="chute-counters"'))
    .replace(/@key="[^"]*"/g, '').replace(/class="@\([^)]*\)"/g, '')
    .replace(/@ChuteTitle\((\d+)\)/g, 'Chute $1');
const animation = fs.readFileSync(sourcePath + '.js', 'utf8').replace(/^export /gm, '');
const css = fs.readFileSync(sourcePath + '.css', 'utf8');
const chutes = Array.from({length:11},(_,i)=>i+1).concat([13,16],Array.from({length:21},(_,i)=>i+21),[98]);
const html = `<!doctype html>
<html lang="fr"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Aperçu — convoyeur du haut</title><style>
${css}
*{box-sizing:border-box}body{margin:0;padding:24px;background:#0b1217;color:#edf5f8;font:15px system-ui,sans-serif}
main{max-width:1640px;margin:auto}h1{margin:8px 0;font-size:28px}p{color:#9fb5c5;line-height:1.5}
.badge{color:#ffc568;font-size:12px;font-weight:bold;letter-spacing:1px}.toolbar{display:flex;gap:14px;align-items:center;flex-wrap:wrap;padding:16px;background:#18242d;border:1px solid #334650;border-radius:8px}
button,select{font:inherit;color:#edf5f8;background:#263941;border:1px solid #526774;border-radius:5px;padding:9px;cursor:pointer}
button:hover{background:#375261}label{display:flex;gap:8px;align-items:center}svg{width:100%;display:block}#status{min-height:24px}.key{display:inline-block;width:12px;height:10px;margin-right:6px;border:1px solid white}
</style></head><body><main><span class="badge">DÉMONSTRATION LOCALE · COLIS FICTIFS</span>
<h1>Convoyeur du haut</h1><p>Même animation que dans l’application. Boucle ≈ 800 pieds, vitesse ≈ 240 pieds/minute. Aucune connexion à l’automate.</p>
<div class="toolbar"><button id="pause">Mettre en pause</button>
<label>Station <select id="station"><option value="0">1 — voie du haut</option><option value="1">2 — voie du bas</option></select></label>
<label>Destination <select id="chute">${chutes.map(c=>`<option value="${c}" ${c===98?'selected':''}>${c===98?'98 — Recirculation':'Chute '+c}</option>`).join('')}</select></label>
<button id="send">Envoyer un colis fictif</button><button id="clear">Vider le plan</button>
<label>Vitesse de démonstration <select id="speed"><option value="1">×1 — temps réel</option><option value="5">×5</option><option value="10" selected>×10 — accélérée</option></select></label>
<label><input type="checkbox" id="auto" checked> Envois automatiques</label></div>
<p id="status" role="status"></p><svg class="conveyor-running" xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1640 830" aria-label="Simulation du convoyeur du haut">${drawing}<g data-parcel-layer="true" aria-hidden="true" pointer-events="none"></g></svg>
<p><span class="key" style="background:#ffcf78"></span>Station 1 &nbsp; <span class="key" style="background:#9edcff"></span>Station 2 · Bleu pâle transparent : destination de colis en route. Nombre affiché dès 2 colis, toutes stations confondues. Les colis 98 disparaissent dans la branche de recirculation, en bas à gauche. À ×10, le temps est accéléré uniquement dans cet aperçu.</p>
</main><script>
// A virtual clock accelerates only this standalone demo, without changing production code.
const realNow=performance.now.bind(performance), realFrame=requestAnimationFrame.bind(window);
let clock=0,lastReal=realNow(),rate=10;
function demoNow(){const now=realNow();clock+=(now-lastReal)*rate;lastReal=now;return clock;}
performance.now=demoNow;
window.requestAnimationFrame=callback=>realFrame(()=>callback(demoNow()));
${animation}
const svg=document.querySelector('svg'), preview=create(svg);
const events=[[],[]], sequences=[0,0];let running=true,index=0;
const status=document.querySelector('#status');
function refresh(){preview.update(running,events[0],events[1]);svg.classList.toggle('conveyor-running',running);}
function send(lane,chute){events[lane].push({sequence:++sequences[lane],chute,parcelKey:lane+':'+sequences[lane]});if(events[lane].length>256)events[lane].shift();refresh();status.textContent='Colis fictif : station '+(lane+1)+' → '+(chute===98?'recirculation 98':'chute '+chute);}
refresh();send(0,98);send(1,23);
document.querySelector('#send').onclick=()=>send(+document.querySelector('#station').value,+document.querySelector('#chute').value);
document.querySelector('#pause').onclick=e=>{running=!running;refresh();e.target.textContent=running?'Mettre en pause':'Reprendre';};
document.querySelector('#clear').onclick=()=>{events[0]=[];events[1]=[];refresh();status.textContent='Plan vidé.';};
document.querySelector('#speed').onchange=e=>{refresh();demoNow();rate=+e.target.value;};
setInterval(()=>{if(running&&document.querySelector('#auto').checked){const choices=[1,98,38,23,16,32,39,98,41];send(index%2,choices[index++%choices.length]);}},2500);
</script></body></html>`;
const output=path.join(__dirname,'apercu-convoyeur.html');
fs.writeFileSync(output,html);
console.log(output);
