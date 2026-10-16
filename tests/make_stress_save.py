"""Build a stress-test save: replace a single-instance script's source with TurboStress.prpl and add
TurboProbe.prpl to the script table. Usage: make_stress_save.py IN.pf1 OUT.pf1 [host_script]"""
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', 'tools'))
import pfnbt  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))


def source(name):
    with open(os.path.join(HERE, name), encoding='utf-8') as f:
        return f.read().replace('\r\n', '\n').replace('\n', '\r\n').encode('utf-8')


def main():
    src, dst = sys.argv[1:3]
    host = sys.argv[3] if len(sys.argv) > 3 else 'Help.prpl'
    root = pfnbt.decode(pfnbt.load(src))
    scripts = next(pfnbt.find(root, 'scripts'))
    names = [e[1] for e in scripts[2]]
    assert host in names, names
    for entry in scripts[2]:
        if entry[1] == host:
            entry[2] = source('TurboStress.prpl')
    if 'TurboProbe.prpl' not in names:
        scripts[2].append([7, 'TurboProbe.prpl', source('TurboProbe.prpl')])
    out = pfnbt.encode(root)
    pfnbt.save(dst, out)
    print('wrote', dst, '- TurboStress runs as', host)


if __name__ == '__main__':
    main()
