"""Drive a GNOME Boxes VM through libvirt: type text, press keys, take screenshots.
Usage: vmctl.py type TEXT | key KEY [KEY...] | shot OUT.png   (VM name from $PF_VM_DOMAIN)"""
import os
import subprocess
import sys

DOMAIN = os.environ.get('PF_VM_DOMAIN', 'win10')
VIRSH = ['host-spawn', 'flatpak', 'run', '--command=virsh', 'org.gnome.Boxes', '-c', 'qemu:///session']
# Written by virsh inside the Boxes sandbox, so it must be a path that sandbox can write.
SHOT = os.path.expanduser('~/.var/app/org.gnome.Boxes/cache/vm.ppm')

PLAIN = {' ': 'KEY_SPACE', '\\': 'KEY_BACKSLASH', '.': 'KEY_DOT', '-': 'KEY_MINUS', '/': 'KEY_SLASH',
         ';': 'KEY_SEMICOLON', "'": 'KEY_APOSTROPHE', ',': 'KEY_COMMA', '=': 'KEY_EQUAL', '\n': 'KEY_ENTER'}
SHIFTED = {':': 'KEY_SEMICOLON', '"': 'KEY_APOSTROPHE', '_': 'KEY_MINUS', '?': 'KEY_SLASH', '|': 'KEY_BACKSLASH'}


def chord(char):
    if char.isalpha():
        return (['KEY_LEFTSHIFT'] if char.isupper() else []) + ['KEY_' + char.upper()]
    if char.isdigit():
        return ['KEY_' + char]
    if char in PLAIN:
        return [PLAIN[char]]
    return ['KEY_LEFTSHIFT', SHIFTED[char]]


def virsh(commands):
    subprocess.run(VIRSH + ['; '.join(commands)], check=True, stdout=subprocess.DEVNULL)


def main():
    action, args = sys.argv[1], sys.argv[2:]
    if action == 'type':
        virsh(['send-key %s --holdtime 30 %s' % (DOMAIN, ' '.join(chord(c))) for c in ' '.join(args)])
    elif action == 'key':
        virsh(['send-key %s --holdtime 50 %s' % (DOMAIN, ' '.join(args))])
    elif action == 'shot':
        virsh(['screenshot %s %s' % (DOMAIN, SHOT)])
        subprocess.run(['ffmpeg', '-loglevel', 'error', '-y', '-i', SHOT, args[0]], check=True)


if __name__ == '__main__':
    main()
