"""Render three review-only PNG proposals from central MySQL aggregate rows."""
import argparse, collections, datetime as dt, json, re
from pathlib import Path
import numpy as np
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
from matplotlib.path import Path as MPath
from matplotlib.patches import PathPatch, FancyBboxPatch
from matplotlib.colors import LinearSegmentedColormap, Normalize

parser = argparse.ArgumentParser()
parser.add_argument('--source', type=int, required=True)
parser.add_argument('--exclude', type=int, nargs='*', default=[])
args = parser.parse_args()
ROOT = Path(__file__).resolve().parent
EXCLUDED = set(args.exclude)
OUTPUT = ROOT/('sans-'+'-'.join(map(str, sorted(EXCLUDED)))) if EXCLUDED else ROOT
OUTPUT.mkdir(exist_ok=True)
rows = json.loads((ROOT/'upper-history.json').read_text(encoding='utf-8-sig'))
weekdays = [dt.date(2026,8,31)+dt.timedelta(days=i) for i in range(28)
            if (dt.date(2026,8,31)+dt.timedelta(days=i)).weekday()<5]
days = [str(day) for day in weekdays]
selected = [r for r in rows if r['evening'] in days]
plan_chutes = list(range(1,12))+[13,16]+list(range(21,42))
chutes = [c for c in plan_chutes if c not in EXCLUDED]
cube = np.zeros((len(days),len(chutes),24))
allcube = np.zeros((len(days),24))
outside = collections.Counter()
excluded = collections.Counter()
for r in selected:
    n = r['passages']
    i,j=days.index(r['evening']),int(r['slot'])
    allcube[i,j]+=n
    if r['chute'] in chutes: cube[i,chutes.index(r['chute']),j]+=n
    elif r['chute'] in plan_chutes: excluded[str(r['chute'])]+=n
    else: outside[str(r['chute'])]+=n
assert len(days)==20 and all(0<=int(r['slot'])<24 for r in selected)
assert int(allcube.sum())==int(cube.sum())+sum(outside.values())+sum(excluded.values())
mean=cube.mean(axis=0)
total=mean.sum(axis=0)
peak=int(total.argmax())
labels=[f'{(16+i//2)%24:02d}:{30*(i%2):02d}' for i in range(25)]
BG='#0c151b'; PANEL='#121f28'; INK='#eaf2f7'; MUTED='#9db0be'; BORDER='#2a3e4a'
cmap=LinearSegmentedColormap.from_list('load',['#203949','#267f98','#4ac6b8','#f3bd53','#ed684e'])
norm=Normalize(0,max(1,float(mean.max())))
plt.rcParams.update({'font.family':'DejaVu Sans','font.size':11,'text.color':INK,
    'axes.labelcolor':MUTED,'xtick.color':MUTED,'ytick.color':MUTED,'axes.edgecolor':BORDER,
    'axes.facecolor':PANEL,'figure.facecolor':BG,'savefig.facecolor':BG})
raw=(ROOT.parents[1]/'new/Conveyor.Web/Components/Layout/ConveyorDiagram.razor').read_text(encoding='utf-8-sig')
paths=dict(re.findall(r'<path id="([^"]+)" d="([^"]+)"',raw))
def mpath(s):
    tok=re.findall(r'[A-Za-z]|-?\d+(?:\.\d+)?',s); verts=[]; codes=[]; x=y=0; i=0
    while i<len(tok):
        op=tok[i]; i+=1
        n={'M':2,'L':2,'H':1,'V':1,'Q':4,'C':6}[op]
        v=list(map(float,tok[i:i+n])); i+=n
        if op in ('M','L'): x,y=v; verts.append((x,y)); codes.append(MPath.MOVETO if op=='M' else MPath.LINETO)
        elif op=='H': x=v[0]; verts.append((x,y)); codes.append(MPath.LINETO)
        elif op=='V': y=v[0]; verts.append((x,y)); codes.append(MPath.LINETO)
        else:
            pts=list(zip(v[::2],v[1::2])); verts.extend(pts); codes.extend([MPath.CURVE3 if op=='Q' else MPath.CURVE4]*len(pts)); x,y=pts[-1]
    return MPath(verts,codes)
tops={1:1420,2:1320,3:1248,4:1152,5:1056,6:960,7:864,8:768,9:672,10:576,11:504,13:432,16:390}
bottom={c:432+(c-21)*48+(48 if c>=24 else 0) for c in range(21,40)}
bottom.update({40:1420,41:1480})
def draw_map(ax, values, numbers=True, small=False):
    ax.set(xlim=(50,1630),ylim=(810,70)); ax.set_aspect('equal'); ax.axis('off')
    for key,d in paths.items():
        c=int(key.split('-')[1]) if re.match(r'chute-\d+-path',key) else None
        col=cmap(norm(values[chutes.index(c)])) if c in chutes else '#263e49'
        ax.add_patch(PathPatch(mpath(d),facecolor='none',edgecolor='#526d7a',lw=5 if small else 8,capstyle='butt'))
        ax.add_patch(PathPatch(mpath(d),facecolor='none',edgecolor=col,lw=3 if small else 5,capstyle='butt'))
    for c in plan_chutes:
        x=tops.get(c,bottom.get(c)); y=205 if c in tops else 360
        if c==16:y=238
        val=values[chutes.index(c)] if c in chutes else None
        ax.text(x,y,str(c),ha='center',va='center',fontsize=6 if small else 8,color=INK if val is not None else MUTED,
                bbox=dict(boxstyle='round,pad=.21',fc='#14232c',ec=cmap(norm(val)) if val is not None else BORDER,lw=.8))
        if numbers:
            cy=125 if c in tops else {35:472,36:493,37:630,38:692,39:790,40:490,41:490}.get(c,442)
            if c==16:cy=200
            ax.text(x,cy,f'{val:.0f}' if val is not None else 'Exclue',ha='center',va='center',fontsize=8,color=INK if val is not None else MUTED,fontweight='bold')
    for y in [510,550]:
        ax.add_patch(FancyBboxPatch((1228,y-17),40,34,boxstyle='round,pad=0,rounding_size=5',ec='#f3bd53',fc='#4b3d25',lw=1))
def base(num,title,subtitle):
    fig=plt.figure(figsize=(16,10),dpi=150)
    fig.text(.035,.962,f'ESQUISSE {num}  /  ST-HUBERT · ACHALANDAGE HISTORIQUE',fontsize=10,color='#4ac6b8',weight='bold')
    fig.text(.035,.923,title,fontsize=24,weight='bold')
    fig.text(.035,.888,subtitle,fontsize=11,color=MUTED)
    fig.text(.035,.044,f'31 août–25 sept. 2026 · 20 soirées lun–ven · 16 h–4 h · convoyeur du haut · DATE_LIV sans conversion',fontsize=9,color=MUTED)
    fig.text(.035,.025,'Dépôt 1 · exception 903 · source_type 200 · hors annulés et doublons exacts · passages · moyenne incluant les zéros',fontsize=8.6,color=MUTED)
    fig.text(.965,.025,'PROPOSITION — aucune modification de l’application',ha='right',fontsize=8,color='#f3bd53')
    scope = ('Chutes '+', '.join(map(str, sorted(EXCLUDED)))+' exclues des couleurs, classements et totaux · échelle recalculée · hors plan à part.'
             if EXCLUDED else 'Chutes du plan uniquement · 11 710 passages hors plan (2,8 %) conservés à part · couleurs = volume, pas capacité.')
    fig.text(.035,.855,scope,fontsize=9,color='#f3bd53' if EXCLUDED else MUTED)
    return fig
def colorbar(fig,box,label):
    ax=fig.add_axes(box); cb=fig.colorbar(plt.cm.ScalarMappable(norm=norm,cmap=cmap),cax=ax,orientation='horizontal')
    cb.set_label(label,fontsize=9); cb.ax.tick_params(labelsize=8); cb.outline.set_visible(False)
def clean(ax):
    ax.spines[['top','right']].set_visible(False); ax.grid(axis='y',color=BORDER,alpha=.6); ax.set_axisbelow(True)
def save(fig,name):
    fig.savefig(OUTPUT/name,dpi=150); plt.close(fig)

# 1: spatial reading for a selected half-hour, plus ranking and time navigation.
fig=base('01','Où ça charge, à une heure précise',f'Tranche sélectionnée : {labels[peak]}–{labels[peak+1]}  •  moyenne par soirée  •  couleurs identiques pour toutes les heures')
ax=fig.add_axes([.03,.285,.72,.565]); draw_map(ax,mean[:,peak])
rank=np.argsort(mean[:,peak])[-5:][::-1]
ax=fig.add_axes([.79,.43,.175,.34]); ax.barh(np.arange(5),mean[rank,peak],color=[cmap(norm(v)) for v in mean[rank,peak]],height=.54)
ax.set_yticks(range(5),[f'Chute {chutes[i]}' for i in rank]); ax.invert_yaxis(); ax.set_xlim(0,mean[rank,peak].max()*1.28)
ax.set_title('Les 5 plus sollicitées',loc='left',color=INK,fontsize=12,pad=18)
for j,v in enumerate(mean[rank,peak]):ax.text(v+1,j,f'{v:.0f}',va='center',fontsize=11)
ax.set_xlabel('Passages moyens / 30 min',fontsize=9); ax.spines[['top','right','left']].set_visible(False)
colorbar(fig,[.1,.285,.53,.014],'Passages moyens par chute / 30 min — intensité de volume, pas seuil de saturation')
ax=fig.add_axes([.06,.12,.88,.1]); ax.bar(np.arange(24),total,color=['#f3bd53' if i==peak else '#31576a' for i in range(24)],width=.85)
ax.set_xticks(np.arange(0,24,2),[labels[i] for i in range(0,24,2)]);ax.set_ylabel('Passages / 30 min',fontsize=9);clean(ax)
fig.text(.06,.237,'Dans l’application : déplacer le curseur pour recolorer le plan.',color=MUTED,fontsize=10)
save(fig,'01-plan-par-heure.png')

# 2: complete chute-by-time overview, natural chute order and shared intensity.
fig=base('02','Quelles chutes sont chargées, et quand ?', 'Une ligne par chute · une colonne par demi-heure · même échelle de couleur pour toute la grille')
ax=fig.add_axes([.085,.15,.75,.675]); im=ax.imshow(mean,aspect='auto',cmap=cmap,norm=norm,interpolation='nearest')
ax.set_yticks(range(len(chutes)),[f'{c:02d}' for c in chutes]);ax.tick_params(axis='y',labelsize=9,length=0)
ax.set_xticks(np.arange(0,24,2),[labels[i] for i in range(0,24,2)]);ax.set_ylabel('Chute')
ax.set_xticks(np.arange(-.5,24,1),minor=True);ax.set_yticks(np.arange(-.5,len(chutes),1),minor=True);ax.grid(which='minor',color=BG,lw=1);ax.tick_params(which='minor',length=0)
for row in range(len(chutes)):
    j=int(mean[row].argmax())
    ax.text(j,row,f'{mean[row,j]:.0f}',ha='center',va='center',color=('#0c151b' if norm(mean[row,j])>.6 else INK),fontsize=7.8,fontweight='bold')
ax2=fig.add_axes([.855,.15,.11,.675]);ax2.set_ylim(len(chutes)-.5,-.5);ax2.set_xlim(0,1);ax2.axis('off')
ax2.text(0,-1.5,'Heure du pic',fontsize=10,color=MUTED)
for i in range(len(chutes)):ax2.text(0,i,labels[int(mean[i].argmax())],fontsize=8.5,va='center')
colorbar(fig,[.32,.093,.35,.013],'Passages moyens / 30 min · chiffre = pic moyen de la chute')
save(fig,'02-grille-chutes-heures.png')

# 3: stable views across phases; equal units and same normalization.
fig=base('03','Préparer les équipes par phase de soirée','Quatre plans côte à côte · mêmes couleurs et unités · le classement change selon la période')
phases=[(0,6,'16 h–19 h'),(6,12,'19 h–22 h'),(12,18,'22 h–1 h'),(18,24,'1 h–4 h')]
for k,(a,b,label) in enumerate(phases):
    left=.035+k*.242; vals=mean[:,a:b].mean(axis=1)
    fig.text(left,.825,label,fontsize=17,weight='bold')
    ax=fig.add_axes([left,.485,.23,.305]);draw_map(ax,vals,False,True)
    ranks=np.argsort(vals)[-3:][::-1]
    fig.text(left,.477,'CHUTES LES PLUS CHARGÉES',fontsize=8.5,color=MUTED)
    for j,idx in enumerate(ranks):
        fig.text(left,.445-j*.031,f'{j+1}. Chute {chutes[idx]}',fontsize=11)
        fig.text(left+.215,.445-j*.031,f'{vals[idx]:.0f} / 30 min',fontsize=10,color=INK,ha='right')
colorbar(fig,[.31,.322,.38,.013],'Passages moyens / 30 min · moyenne sur chaque phase de 3 heures')
ax=fig.add_axes([.06,.12,.88,.13]);low,high=np.percentile(cube.sum(axis=1),[25,75],axis=0)
ax.fill_between(range(24),low,high,color='#4ac6b8',alpha=.18,label='Intervalle P25–P75 entre soirées')
ax.plot(range(24),total,color='#4ac6b8',lw=2,label='Moyenne des 20 soirées')
ax.set_xticks(range(0,24,2),[labels[i] for i in range(0,24,2)]);ax.set_ylabel('Passages / 30 min',fontsize=9);clean(ax)
ax.legend(loc='upper right',fontsize=8,facecolor=PANEL,edgecolor=BORDER,labelcolor=INK)
fig.text(.06,.275,'Variabilité du volume total sur les chutes représentées',fontsize=11)
save(fig,'03-phases-de-soiree.png')

summary={'source_ids':[None,1],'evenings':days,'passages':int(allcube.sum()),'mapped_passages':int(cube.sum())+sum(excluded.values()),
 'displayed_passages':int(cube.sum()),'excluded_chutes':dict(excluded),'color_scale_max':float(norm.vmax),
 'outside_map':dict(outside),'exact_duplicates_removed':sum(r['raw_rows']-r['passages'] for r in selected),
 'peak_half_hour':labels[peak],'peak_mapped_mean':float(total[peak]),
 'top_at_peak':[{ 'chute':chutes[i],'mean':float(mean[i,peak])} for i in rank],
 'within_bucket_repeats':sum(r['passages']-r['distinct_parcels'] for r in selected)}
(OUTPUT/'summary.json').write_text(json.dumps(summary,indent=2,ensure_ascii=False),encoding='utf-8')
print(json.dumps(summary,ensure_ascii=False))
