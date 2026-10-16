"""Diff two Particle Fleet .pf1 saves field by field. Usage: compare_saves.py A.pf1 B.pf1 [max_diffs]"""
import struct
import sys

import pfnbt

FLOATS = {2: '<f', 6: '<d', 0xea: '<2f', 0xeb: '<3f', 0xec: '<4f'}


def fmt(t, v):
    if t in FLOATS:
        return struct.unpack(FLOATS[t], v)
    if t == 1:
        return struct.unpack('<i', v)[0]
    return v if len(repr(v)) < 80 else repr(v)[:77] + '...'


def diff(a, b, path, out):
    ta, na, va = a
    tb, nb, vb = b
    if ta != tb:
        out.append((path, 'type %#x' % ta, 'type %#x' % tb))
    elif ta == 0x0a:
        da = {c[1]: c for c in va}
        db = {c[1]: c for c in vb}
        for k in da.keys() | db.keys():
            if k not in da or k not in db:
                out.append((path + '/' + k, 'present' if k in da else 'missing', 'present' if k in db else 'missing'))
            else:
                diff(da[k], db[k], path + '/' + k, out)
    elif ta == 4:
        (ea, xa), (eb, xb) = va, vb
        if len(xa) != len(xb):
            out.append((path, 'len %d' % len(xa), 'len %d' % len(xb)))
        for i, (x, y) in enumerate(zip(xa, xb)):
            diff([ea, '', x], [eb, '', y], '%s[%d]' % (path, i), out)
    elif va != vb:
        out.append((path, fmt(ta, va), fmt(tb, vb)))


def main():
    a, b = (pfnbt.decode(pfnbt.load(p)) for p in sys.argv[1:3])
    limit = int(sys.argv[3]) if len(sys.argv) > 3 else 30
    out = []
    diff(a, b, '', out)
    print('%d differences' % len(out))
    for path, x, y in out[:limit]:
        print('  %s\n      A: %s\n      B: %s' % (path, x, y))


if __name__ == '__main__':
    main()
