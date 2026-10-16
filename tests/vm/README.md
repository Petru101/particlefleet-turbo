# Windows VM test

Runs the release zip on Windows without Steam or Steam Cloud being involved, then auto-runs the self-test.

1. `packaging/package.sh`, then build a CD image:
   `genisoimage -J -R -V PFTEST -o tests/vm/pftest.iso -graft-points setup.ps1=tests/vm/cd/setup.ps1 run.bat=tests/vm/cd/run.bat ParticleFleetTurbo-0.1.0.zip=dist/ParticleFleetTurbo-0.1.0.zip stress.pf1=<stress save>`
   (make the stress save with `tests/make_stress_save.py`).
2. Insert it into the VM (GNOME Boxes):
   `host-spawn flatpak run --command=virsh org.gnome.Boxes -c qemu:///session change-media <domain> sdc <iso> --live`
3. Run `<CD drive>:\run.bat` in the VM (`vmctl.py key KEY_LEFTMETA KEY_R`, `vmctl.py type 'E:\run.bat'`).
4. Read `C:\PFTest\summary.txt` (Notepad opens it; `vmctl.py shot out.png` to look).

`setup.ps1` needs the game installed through Steam in the VM. It copies it to `C:\PFTest\game`, starts it directly
with `steam_appid.txt = 480`, redirects the game's data folder (`SelfTest.DataDir`), and verifies/restores the real
cloud-synced `Documents\My Games\particlefleet`. Don't launch the test through Steam: with the account active on
another PC, Steam shows a "Cloud Out of Date" conflict prompt.
