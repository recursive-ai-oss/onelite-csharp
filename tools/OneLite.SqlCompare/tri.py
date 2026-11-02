#!/usr/bin/env python3
"""Groups the mismatches written by --out by native message and our message: tri.py <file> [rows]."""
import sys,re,collections
L=open(sys.argv[1]).read().split('\n')
c=collections.Counter();ex={}
i=0
while i<len(L):
    if L[i].startswith('['):
        k=L[i].split(']')[0]
        n=L[i+1].strip() if i+1<len(L) else '';o=L[i+2].strip() if i+2<len(L) else ''
        n=re.sub(r'off=\d+','off=N',re.sub(r'tail=\d+','tail=N',n));o=re.sub(r'off=\d+','off=N',re.sub(r'tail=\d+','tail=N',o))
        key=(k,n[:90],o[:70]);c[key]+=1
        full=i+3<len(L) and L[i+3].startswith('    full:')
        ex.setdefault(key,(L[i+3].strip()[6:] if full else L[i])[:int(sys.argv[3]) if len(sys.argv)>3 else 230])
        i+=4 if full else 3
    else: i+=1
for key,v in c.most_common(int(sys.argv[2]) if len(sys.argv)>2 else 30):
    print(v,key);print('     ',ex[key])
