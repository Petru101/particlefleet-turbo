"""Particle Fleet .pf1 reader/writer: 4-byte LE uncompressed size + gzip of a little-endian NBT variant."""
import gzip, io, struct, zlib

FIXED = {8: 1, 5: 2, 1: 4, 9: 8, 2: 4, 6: 8, 0xea: 8, 0xeb: 12, 0xec: 16, 0xf5: 8, 0xf6: 8}
ARR = {7: 1, 0x0b: 4, 0xee: 8, 0xef: 4, 0xf0: 2, 0xf1: 1, 0xf2: 8, 0xf3: 4, 0xf4: 8, 0xf7: 2, 0xe8: 8, 0xe9: 12}


class R:
    def __init__(s, b): s.b, s.p = b, 0
    def take(s, n):
        v = s.b[s.p:s.p + n]; s.p += n
        if len(v) != n: raise EOFError(s.p)
        return v
    def u8(s): return s.take(1)[0]
    def u16(s): return struct.unpack('<H', s.take(2))[0]
    def i32(s): return struct.unpack('<i', s.take(4))[0]
    def str(s): return s.take(s.u16()).decode('utf-8')


def read_payload(r, t):
    if t in FIXED: return r.take(FIXED[t])
    if t in ARR: return r.take(r.i32() * ARR[t])
    if t == 3: return r.str()
    if t == 0xe7: return [r.str() for _ in range(r.i32())]
    if t == 4:
        et = r.u8(); n = r.i32()
        return (et, [read_payload(r, et) for _ in range(n)])
    if t == 0x0a:
        items = []
        while True:
            ct = r.u8()
            if ct == 0: return items
            items.append([ct, r.str(), read_payload(r, ct)])
    raise ValueError('unknown tag type %#x at %d' % (t, r.p))


def write_payload(o, t, v):
    if t in FIXED: o.write(v); return
    if t in ARR: o.write(struct.pack('<i', len(v) // ARR[t])); o.write(v); return
    if t == 3:
        b = v.encode('utf-8'); o.write(struct.pack('<H', len(b))); o.write(b); return
    if t == 0xe7:
        o.write(struct.pack('<i', len(v)))
        for x in v: write_payload(o, 3, x)
        return
    if t == 4:
        et, xs = v; o.write(bytes([et])); o.write(struct.pack('<i', len(xs)))
        for x in xs: write_payload(o, et, x)
        return
    if t == 0x0a:
        for ct, name, val in v:
            o.write(bytes([ct])); write_payload(o, 3, name); write_payload(o, ct, val)
        o.write(b'\0'); return
    raise ValueError(t)


def decode(raw):
    r = R(raw)
    t = r.u8(); name = r.str(); v = read_payload(r, t)
    assert r.p == len(raw), (r.p, len(raw))
    return [t, name, v]


def encode(root):
    o = io.BytesIO(); t, name, v = root
    o.write(bytes([t])); write_payload(o, 3, name); write_payload(o, t, v)
    return o.getvalue()


def load(path):
    d = open(path, 'rb').read()
    return zlib.decompressobj(31).decompress(d[4:])


def save(path, raw):
    open(path, 'wb').write(struct.pack('<i', len(raw)) + gzip.compress(raw, mtime=0))


def find(node, name):
    """Yield every [type, name, value] entry with the given name, depth-first."""
    t, n, v = node
    if n == name: yield node
    if t == 0x0a:
        for c in v: yield from find(c, name)
    elif t == 4 and v[0] == 0x0a:
        for x in v[1]: yield from find([0x0a, '', x], name)
