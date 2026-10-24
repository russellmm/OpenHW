// Drives the OpenHW tray-hover extension inside the headless test shell (tools/tray-hover-test/run.sh): sets hover
// on the fake items' panel buttons and checks what the tooltip shows. Prints "HOVERTEST PASS|FAIL <check>" lines,
// then "HOVERTEST DONE"; screenshots go to $HOVER_OUT.

import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Shell from 'gi://Shell';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as PopupMenu from 'resource:///org/gnome/shell/ui/popupMenu.js';
import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';

Gio._promisify(Shell.Screenshot.prototype, 'screenshot');

const UUID = 'openhw-tray-hover@russellmm.github.io';
const OUT = GLib.getenv('HOVER_OUT');
const log = msg => console.log(`HOVERTEST ${msg}`);
const check = (name, ok, detail) => log(`${ok ? 'PASS' : 'FAIL'} ${name}${ok ? '' : ` — ${detail}`}`);
const wait = ms => new Promise(r => GLib.timeout_add(GLib.PRIORITY_DEFAULT, ms, () => {
    r();
    return GLib.SOURCE_REMOVE;
}));

const idOf = role => new Promise(resolve => {
    const [bus, path = '/StatusNotifierItem'] = role.slice('appindicator-'.length).split('@');
    Gio.DBus.session.call(bus, path, 'org.freedesktop.DBus.Properties', 'Get',
        new GLib.Variant('(ss)', ['org.kde.StatusNotifierItem', 'Id']), null, 0, 2000, null, (c, r) => {
            try {
                resolve(c.call_finish(r).recursiveUnpack()[0]);
            } catch {
                resolve(null);
            }
        });
});

async function buttons() {
    const found = {};
    for (const [role, button] of Object.entries(Main.panel.statusArea)) {
        if (role.startsWith('appindicator-'))
            found[await idOf(role)] = button;
    }
    return found;
}

const tip = () => Main.uiGroup.get_children().find(a => a.style_class === 'openhw-tray-tooltip');
const shown = () => tip()?.visible === true;
const title = () => tip()?.get_children()[0].text ?? '';
const describe = () => {
    const t = tip();
    if (!t)
        return 'no tooltip actor';
    const [head, body] = t.get_children();
    return `visible=${t.visible} at ${t.x},${t.y} ${t.width}x${t.height} title=${JSON.stringify(head.text)} body=${JSON.stringify(body.text)}`;
};

async function shot(name) {
    const stream = Gio.File.new_for_path(`${OUT}/${name}.png`).replace(null, false, Gio.FileCreateFlags.NONE, null);
    await new Shell.Screenshot().screenshot(false, stream);
    stream.close(null);
}

export default class Harness extends Extension {
    enable() {
        // GNOME re-enables extensions enabled after one it disables, so this can be enabled twice: run once.
        if (globalThis.openhwHoverTestStarted)
            return;
        globalThis.openhwHoverTestStarted = true;
        this._run().catch(e => log(`FAIL harness — ${e}\n${e.stack}`)).finally(() => log('DONE'));
    }

    disable() {}

    async _run() {
        await wait(6000);
        Main.overview.hide();
        await wait(1500);
        let icons = await buttons();
        const ours = icons['openhw-tray-0'], other = icons['other-app'];
        check('both fake icons are in the panel', ours && other, Object.keys(icons).join(', '));

        ours.hover = true;
        await wait(150);
        check('nothing before the delay', !shown(), describe());
        await wait(600);
        check('tooltip after the delay', shown() && title().includes('GPU Temperature'), describe());
        const [bx] = ours.get_transformed_position();
        check('tooltip below the icon', tip().y >= ours.height && Math.abs(tip().x + tip().width / 2 - (bx + ours.width / 2)) < 2 ||
            tip().x + tip().width === global.stage.width, describe());
        check('long explanations wrap', tip().width <= 560 && tip().height > 100, describe());
        log(`hovering: ${describe()}`);
        await shot('hover');
        const before = title();
        await wait(2200);
        check('updates live while hovering', shown() && title() !== before, `${before} → ${title()}`);

        ours.hover = false;
        await wait(200);
        check('hidden when the pointer leaves', !shown(), describe());

        other.hover = true;
        await wait(800);
        check('no tooltip for other apps', !shown(), describe());
        other.hover = false;

        ours.hover = true;
        await wait(800);
        check('shows again on the next hover', shown(), describe());
        ours.menu.addMenuItem(new PopupMenu.PopupMenuItem('test item')); // an empty menu never opens
        ours.menu.open();
        await wait(100);
        check('hidden when its menu opens', ours.menu.isOpen && !shown(), `menu open=${ours.menu.isOpen}; ${describe()}`);
        ours.menu.close();
        ours.hover = false;
        await wait(200);

        Main.extensionManager.disableExtension(UUID);
        await wait(300);
        check('disabling removes the tooltip', !tip(), describe());
        Main.extensionManager.enableExtension(UUID);
        await wait(3000);
        icons = await buttons(); // GNOME re-creates the tray icons on extension changes
        icons['openhw-tray-0'].hover = true;
        await wait(800);
        check('works again after re-enabling', shown(), describe());
    }
}
