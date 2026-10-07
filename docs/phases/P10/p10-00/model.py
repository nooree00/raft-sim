# P10-00: a model of a harness shard's time (slowest worker ready, then the slowest worker's entries,
# after the two-way barrier), and the assignments compared. Run: python3 -I model.py
import re, os, sys, glob, statistics as st
REPO=os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)),'..','..','..','..'))
def spec(id):
    d={}
    for l in open(f'{REPO}/sabotage/{id}/sabotage.txt'):
        if ': ' in l: k,v=l.rstrip('\n').split(': ',1); d[k]=v
    return d
def unit_of(id):
    d=spec(id)
    if d.get('kind')=='test': return 'test:'+d['project']
    return 'command:'+(d.get('baseline') or d.get('command'))
def unit_label(u):  # as printed in split lines
    if u.startswith('test:'): return os.path.splitext(os.path.basename(u[5:]))[0]
    return "'"+u[8:]+"'"
# The measured costs (entry-costs.txt, unit-costs.txt), from one local run of all 11 shards.
HERE=os.path.dirname(os.path.abspath(__file__))
cost={l.split()[0]:float(l.split()[1]) for l in open(f'{HERE}/entry-costs.txt') if not l.startswith('#')}
UC={}; B=None
for l in open(f'{HERE}/unit-costs.txt'):
    if l.startswith('#'): continue
    k,v=l.rstrip('\n').rsplit(' ',1)
    if k=='build': B=float(v)
    else: UC[k]=int(v)
shards={}
ids=sorted(cost)
def ucost(u): return UC.get(unit_label(u),5)
def shard_time(sids,W=4,lpt=False):
    sids=sorted(sids)
    w=min(W,len(sids))
    if lpt:
        shares=[[] for _ in range(w)]; sums=[0]*w
        for s in sorted(sids,key=lambda s:-cost[s]):
            k=sums.index(min(sums)); shares[k].append(s); sums[k]+=cost[s]
    else:
        shares=[sids[k::w] for k in range(w)]
    units=sorted({unit_of(s) for s in sids})
    ush=[units[k::w] for k in range(w)]
    ready=max(B+sum(ucost(u) for u in us) for us in ush)
    return ready+max(sum(cost[s] for s in sh) for sh in shares)
def rr(n): return [ids[k::n] for k in range(n)]
def balanced(n,lpt=False):
    sh=[[] for _ in range(n)]
    for s in sorted(ids,key=lambda s:-cost[s]):
        best=min(range(n),key=lambda k: shard_time(sh[k]+[s],lpt=lpt))
        sh[best].append(s)
    return sh
def report(name,plan,lpt=False):
    t=[shard_time(s,lpt=lpt) for s in plan]
    print(f'{name:38s} shards {len(plan):2d}  max {max(t):5.0f}  min {min(t):5.0f}  sum {sum(t):6.0f}  slowest {sorted(t)[-3:]}')
    return t
if __name__=='__main__':
    print('entries',len(ids),'sum',round(sum(cost.values())),'median build',B)
    if shards: print('validation (model vs measured, current RR 11):')
    for i,(tot,w) in sorted(shards.items()):
        print(f'  shard {i:2d} measured {tot:4d} model {shard_time(rr(11)[i-1]):5.0f}')
    report('round-robin by id, 11 (today)',rr(11))
    report('round-robin by id, 12 (size 25)',rr(12))
    report('round-robin by id, 13',rr(13))
    report('cost-balanced, 11',balanced(11))
    report('cost-balanced, 10',balanced(10))
    report('cost-balanced, 11, workers by cost',balanced(11,True),True)
    report('cost-balanced, 9, workers by cost',balanced(9,True),True)
