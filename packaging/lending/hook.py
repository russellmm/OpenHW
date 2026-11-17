#!/usr/bin/python3
"""OpenHW GPU Lending: libvirt handoff hook (qemu.d/50-windows-gpu runs it). No libvirt calls here: hooks run inside
its locks. Based on RTXinVM's hook, but the card and the VM come from OpenHW's settings (/etc/openhw/card-filter.json,
written by openhw-helper): the card by its GPU UUID and its current PCI address (the helper follows the UUID to
whatever slot the card sits in at each start), so nothing here names a slot."""
import glob
import errno
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import syslog
import time
import xml.etree.ElementTree as ET

BASE = Path('/sys/bus/pci/devices')
RUNDIR = Path('/run/windows-gpu-handoff')
STATE = RUNDIR / 'state.json'
SETTINGS = Path('/etc/openhw/card-filter.json')
if os.geteuid() != 0 and os.environ.get('OPENHW_HOOK_SETTINGS'):
    SETTINGS = Path(os.environ['OPENHW_HOOK_SETTINGS'])  # tests only; never as root

VM = GPU = AUDIO = UUID = None


def load_settings():
    """Fills VM, GPU, AUDIO and UUID from OpenHW's settings; False when lending isn't set up."""
    global VM, GPU, AUDIO, UUID
    try:
        s = json.loads(SETTINGS.read_text())
    except FileNotFoundError:
        return False
    if s.get('Uninstalled') or not s.get('Card') or not s.get('CardUuid') or not s.get('Vm'):
        return False
    VM, GPU, UUID = s['Vm'], s['Card'].lower(), s['CardUuid']
    AUDIO = GPU[:-1] + '1'  # the card's display-audio function
    return True


def address(pci):
    domain, bus, rest = pci.split(':')
    slot, function = rest.split('.')
    return int(domain, 16), int(bus, 16), int(slot, 16), int(function, 16)


def log(message):
    syslog.openlog('windows-gpu-handoff')
    syslog.syslog(syslog.LOG_NOTICE, message)
    print(message, file=sys.stderr, flush=True)


def run(*args):
    p = subprocess.run(args, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                       stderr=subprocess.PIPE, text=True, timeout=25)
    if p.returncode:
        raise RuntimeError(f'{args[0]} failed: {p.stderr.strip() or p.stdout.strip()}')
    return p.stdout.strip()


def smi(*args):
    return run('/usr/bin/nvidia-smi', '-i', UUID, *args)


def driver(pci):
    p = BASE / pci / 'driver'
    return p.resolve().name if p.exists() else None


def gpu_info():
    return ET.fromstring(smi('-q', '-x')).find('gpu')


def power_limit():
    # Query interface handles XML layout changes between NVIDIA releases.
    value = smi('--query-gpu=power.limit', '--format=csv,noheader,nounits')
    float(value)  # Reject N/A or any other non-numeric response.
    return value


def restore_reset_methods(saved):
    path = BASE / GPU / 'reset_method'
    # Rewriting an unchanged list can re-probe bus reset while the audio
    # function is host-bound, and the kernel then rejects it as unsupported.
    if path.read_text().strip() != saved:
        try:
            path.write_text(saved + '\n')
        except OSError as exc:
            if exc.errno != errno.EINVAL:
                raise
            # Reset availability can change after binding a host driver. Rebuild
            # the supported list, then retain only methods in the saved policy.
            # This queries capabilities; it does not reset the GPU.
            path.write_text('default\n')
            supported = path.read_text().split()
            restorable = [method for method in saved.split() if method in supported]
            if 'flr' not in restorable:
                raise RuntimeError('Function-level reset is no longer available') from exc
            if restorable != supported:
                path.write_text(' '.join(restorable) + '\n')
            log(f'Reset methods now supported by kernel: {" ".join(restorable)}; previously {saved}')


def verify_hardware():
    if not (BASE / GPU).exists():
        raise RuntimeError(f'No device at {GPU}; let openhw-helper find the card again (restart it)')
    if (BASE / GPU / 'vendor').read_text().strip() != '0x10de':
        raise RuntimeError('Unexpected GPU vendor')
    group = {p.name for p in (BASE / GPU / 'iommu_group/devices').iterdir()}
    if GPU not in group or not group <= {GPU, AUDIO}:
        raise RuntimeError(f'IOMMU group holds other devices: {sorted(group)}')
    if driver(GPU) != 'nvidia':
        raise RuntimeError(f'GPU is not available on Linux (driver={driver(GPU)})')
    info = gpu_info()
    if info.findtext('uuid') != UUID or Path(f'/proc/driver/nvidia/gpus/{GPU}/information').read_text().find(UUID) < 0:
        raise RuntimeError(f'The card at {GPU} is not {UUID}')
    return info


def device_clients():
    # NVIDIA indices can change. Resolve the minor from the physical PCI address.
    info = Path(f'/proc/driver/nvidia/gpus/{GPU}/information').read_text()
    minor = next(line.split(':', 1)[1].strip() for line in info.splitlines()
                 if line.startswith('Device Minor:'))
    nodes = [f'/dev/nvidia{minor}']
    nodes += [f'/dev/dri/{p.name}' for p in (BASE / GPU / 'drm').glob('*')
              if p.name.startswith(('card', 'renderD'))]
    for p in (BASE / AUDIO / 'sound').glob('card*'):
        card = p.name[4:]
        nodes += glob.glob(f'/dev/snd/*C{card}*')
    numbers = set()
    for node in nodes:
        try:
            s = os.stat(node)
            if stat.S_ISCHR(s.st_mode):
                numbers.add(s.st_rdev)
        except FileNotFoundError:
            pass
    clients = {}
    for proc in Path('/proc').iterdir():
        if not proc.name.isdigit() or int(proc.name) == os.getpid():
            continue
        try:
            for fd in (proc / 'fd').iterdir():
                try:
                    s = fd.stat()
                    if stat.S_ISCHR(s.st_mode) and s.st_rdev in numbers:
                        clients[proc.name] = (proc / 'comm').read_text().strip()
                        break
                except (FileNotFoundError, ProcessLookupError):
                    continue
        except (FileNotFoundError, ProcessLookupError):
            continue
    return clients


def check_processes(info):
    processes = [(p.findtext('pid'), p.findtext('process_name'))
                 for p in info.findall('./processes/process_info')]
    if processes:
        raise RuntimeError('GPU is busy; close its applications first: ' + str(processes))
    if info.findtext('display_active') == 'Enabled':
        raise RuntimeError('Selected GPU has an active Linux display')


def restore():
    if not STATE.exists():
        return
    saved = json.loads(STATE.read_text())
    if saved['gpu_uuid'] != UUID or saved['pci_address'] != GPU:
        raise RuntimeError('Saved handoff state does not match this GPU')
    # Libvirt managed=yes reattaches the GPU before release/end. Do not try to
    # forcibly unbind it here if that failed; preserve the record for recovery.
    if driver(GPU) != 'nvidia':
        raise RuntimeError(f'GPU did not return to NVIDIA (driver={driver(GPU)}); saved settings retained in {STATE}')
    errors = []
    for label, action in [
        ('power limit', lambda: smi('-pl', saved['power_limit_w'])),
        ('persistence mode', lambda: smi('-pm', '1' if saved['persistence'] else '0')),
        ('reset methods', lambda: restore_reset_methods(saved['reset_method'])),
    ]:
        try:
            action()
        except Exception as exc:
            errors.append(f'{label}: {exc}')
    previous_audio = saved['audio_driver']
    try:
        current_audio = driver(AUDIO)
        if previous_audio and current_audio is None:
            Path(f'/sys/bus/pci/drivers/{previous_audio}/bind').write_text(AUDIO)
        elif current_audio != previous_audio:
            raise RuntimeError(f'Audio driver changed unexpectedly: {current_audio}')
    except Exception as exc:
        errors.append(f'audio driver: {exc}')
    if errors:
        raise RuntimeError('; '.join(errors))
    actual = gpu_info()
    actual_power = power_limit()
    if abs(float(actual_power) - float(saved['power_limit_w'])) > 0.1:
        raise RuntimeError(f'Power limit restore verification failed: {actual_power}')
    if (actual.findtext('persistence_mode') == 'Enabled') != saved['persistence']:
        raise RuntimeError('Persistence restore verification failed')
    log(f'Returned {GPU} to Linux; restored {saved["power_limit_w"]} W and persistence={saved["persistence"]}')
    STATE.unlink()


def prepare():
    if STATE.exists():
        raise RuntimeError(f'Previous handoff record remains at {STATE}; recover it before starting again')
    info = verify_hardware()
    check_processes(info)
    power = power_limit()
    reset_method = (BASE / GPU / 'reset_method').read_text().strip()
    if 'flr' not in reset_method.split():
        raise RuntimeError('GPU has no function-level reset; refusing handoff')
    saved = dict(gpu_uuid=UUID, pci_address=GPU, power_limit_w=power,
                 persistence=info.findtext('persistence_mode') == 'Enabled',
                 audio_driver=driver(AUDIO), reset_method=reset_method,
                 saved_at=time.time())
    RUNDIR.mkdir(mode=0o700, exist_ok=True)
    with STATE.open('x') as f:
        os.chmod(STATE, 0o600)
        json.dump(saved, f, indent=2)
        f.flush()
        os.fsync(f.fileno())
    try:
        smi('-pm', '0')
        # The persistence daemon must have closed this GPU's descriptors too.
        clients = device_clients()
        if clients:
            raise RuntimeError('Linux still has the selected GPU/audio open: ' + str(clients))
        check_processes(gpu_info())
        # The audio function lacks FLR. Leave it unbound on the host rather than
        # assign it to Windows and invite a secondary bus reset on Blackwell.
        if saved['audio_driver']:
            (BASE / AUDIO / 'driver/unbind').write_text(AUDIO)
        (BASE / GPU / 'reset_method').write_text('flr')
        log(f'Prepared {GPU} ({UUID}) for {VM}; saved power limit {saved["power_limit_w"]} W, persistence={saved["persistence"]}')
    except Exception as original:
        try:
            restore()
        except Exception as cleanup:
            raise RuntimeError(f'{original}; cleanup also failed: {cleanup}') from original
        raise


def has_card(xml):
    """True when the VM's definition passes this card's function 0 through."""
    want = address(GPU)
    for a in xml.findall('./devices/hostdev/source/address'):
        try:
            got = (int(a.get('domain', '0'), 0), int(a.get('bus'), 0), int(a.get('slot'), 0), int(a.get('function'), 0))
        except (TypeError, ValueError):
            continue
        if got == want:
            return True
    return False


def main():
    if not load_settings():
        return  # GPU Lending isn't set up (or is uninstalled): leave every VM alone
    if len(sys.argv) == 2 and sys.argv[1] == '--recover':
        if os.geteuid() != 0:
            raise RuntimeError('Recovery requires administrator privileges')
        # Recovery is invoked outside the libvirt hook, so checking VM state is
        # safe here. Never restore settings while Windows is running.
        state = run('/usr/bin/virsh', '-c', 'qemu:///system', 'domstate', VM)
        if state.strip() != 'shut off':
            raise RuntimeError('Shut down the VM fully before recovery')
        restore()
        return
    if len(sys.argv) < 4 or sys.argv[1] != VM:
        return
    operation, phase = sys.argv[2:4]
    if (operation, phase) not in [('prepare', 'begin'), ('release', 'end')]:
        return
    xml = ET.fromstring(sys.stdin.read())
    if not has_card(xml):
        if operation == 'prepare':
            log(f'{VM} does not pass {GPU} through; nothing to hand off')
        return
    if os.geteuid() != 0:
        raise RuntimeError('Handoff hook requires administrator privileges')
    if operation == 'prepare':
        prepare()
    else:
        restore()


if __name__ == '__main__':
    try:
        main()
    except Exception as exc:
        log(f'Handoff stopped: {exc}')
        sys.exit(1)
