import struct, sys, math, cmath

def load(path):
    data = open(path, 'rb').read()
    i = data.find(b'data')
    n = struct.unpack('<I', data[i+4:i+8])[0]
    raw = data[i+8:i+8+n]
    return struct.unpack('<%df' % (len(raw)//4), raw)

x = load(sys.argv[1])[48000*3:]
x = x[:48000*6]
rate = 48000.0
N = len(x)
win = [0.5 - 0.5 * math.cos(2 * math.pi * i / (N - 1)) for i in range(N)]
def tone(hz):
    acc = 0j
    w = -2j * math.pi * hz / rate
    for i in range(0, N):
        acc += x[i] * win[i] * cmath.exp(w * i)
    return 2 * abs(acc) / (N * 0.5)
db = lambda a: 20 * math.log10(a + 1e-15)
total = math.sqrt(sum(v * v for v in x) / N)
print('overall: %.1f dBFS RMS' % db(total))
hum_power = 0
out = []
for k in range(1, 21):
    a = tone(50.0 * k)
    hum_power += a * a / 2
    out.append('%d Hz %.0f' % (50 * k, db(a)))
print('mains hum and overtones (dBFS): ' + ', '.join(out))
print('hum + overtones together: %.1f dBFS RMS = %.0f%% of the signal energy' % (db(math.sqrt(hum_power)), 100 * hum_power / (total * total)))
rest = math.sqrt(max(total * total - hum_power, 1e-30))
print('everything else (hiss): %.1f dBFS RMS' % db(rest))
for f in (36.6, 54.9, 98.1, 1000.0, 2000.0):
    print('  %.1f Hz: %.0f dBFS' % (f, db(tone(f))))
