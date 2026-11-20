import struct, sys, math, collections

path = sys.argv[1]
data = open(path, 'rb').read()
# find "data" chunk
i = data.find(b'data')
n = struct.unpack('<I', data[i+4:i+8])[0]
raw = data[i+8:i+8+n]
x = struct.unpack('<%df' % (len(raw)//4), raw)
rate = 48000
print('samples', len(x), '=', round(len(x)/rate, 2), 's')

zeros = sum(1 for v in x if v == 0.0)
print('exact zero samples: %.1f%%' % (100*zeros/len(x)))
nz = sorted(set(abs(v) for v in x if v != 0.0))
print('smallest non-zero values:', ['%.3e' % v for v in nz[:6]])
step = nz[0]
print('smallest step = %.3e = 1/%.0f  (16-bit LSB = 1/32768 = %.3e)' % (step, 1/step, 1/32768))
levels = collections.Counter(round(v/step) for v in x)
print('distinct levels:', len(levels), ' most common (level: share):', [(k, '%.1f%%' % (100*c/len(x))) for k, c in levels.most_common(8)])

# bursts: runs of non-zero activity (allowing gaps < 5 ms)
gap = rate*5//1000
bursts = []; start = None; last = None
for idx, v in enumerate(x):
    if v != 0.0:
        if start is None: start = idx
        last = idx
    elif start is not None and idx - last > gap:
        bursts.append((start, last)); start = None
if start is not None: bursts.append((start, last))
print('bursts:', len(bursts))
durs = [(b-a+1)/rate*1000 for a, b in bursts]
if durs:
    print('burst length ms: min %.1f  median %.1f  max %.1f' % (min(durs), sorted(durs)[len(durs)//2], max(durs)))
    gaps = [(bursts[k+1][0]-bursts[k][1])/rate*1000 for k in range(len(bursts)-1)]
    if gaps: print('silence between bursts ms: min %.1f  median %.1f  max %.1f' % (min(gaps), sorted(gaps)[len(gaps)//2], max(gaps)))
    print('first bursts (start s, length ms, peak dBFS):')
    for a, b in bursts[:12]:
        pk = max(abs(v) for v in x[a:b+1])
        print('   %.3f s  %.1f ms  %.0f dBFS' % (a/rate, (b-a+1)/rate*1000, 20*math.log10(pk)))
# per-second RMS
print('RMS per second (dBFS):', ' '.join('%.0f' % (10*math.log10(sum(v*v for v in x[s*rate:(s+1)*rate])/rate + 1e-20)) for s in range(len(x)//rate)))
# a look at one burst's samples in LSB units
if bursts:
    a, b = max(bursts, key=lambda ab: max(abs(v) for v in x[ab[0]:ab[1]+1]))
    seg = x[a:min(b+1, a+64)]
    print('loudest burst, first 64 samples in steps:', [round(v/step) for v in seg])
