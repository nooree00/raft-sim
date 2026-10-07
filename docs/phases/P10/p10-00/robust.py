# P10-00: the assignments scored under random cost errors (each entry's cost times 1 ± u). Run: python3 -I robust.py
import os, sys; sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import random, model
from model import *
base=dict(cost)
plans={'rr11':(rr(11),False),'rr12':(rr(12),False),'bal11w':(balanced(11,True),True),'bal10w':(balanced(10,True),True),'bal11':(balanced(11),False)}
for spread in (0.3,0.5):
    res={k:[] for k in plans}
    for trial in range(200):
        rnd=random.Random(trial)
        for s in base: model.cost[s]=base[s]*(1+rnd.uniform(-spread,spread))
        for k,(p,l) in plans.items(): res[k].append(max(shard_time(s,lpt=l) for s in p))
    for s in base: model.cost[s]=base[s]
    print(f'costs ±{int(spread*100)}%: '+'  '.join(f'{k} median {sorted(v)[100]:.0f} p95 {sorted(v)[190]:.0f}' for k,v in res.items()))
