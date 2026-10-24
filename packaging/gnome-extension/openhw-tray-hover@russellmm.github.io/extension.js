// OpenHW tray hover: GNOME's tray (the Ubuntu AppIndicators extension) never shows a StatusNotifierItem's ToolTip,
// so this shows it for OpenHW's icons. OpenHW already publishes the text (reading, min/max/avg, device, explanation)
// and signals NewToolTip after each poll, so the tooltip stays live while the pointer rests on the icon.
//
// The only thing taken from the other extension is how it names its panel buttons: Main.panel.statusArea holds
// each icon as "appindicator-<bus name>@<object path>" (or "appindicator-<bus name>"). Everything else is asked of
// the item itself over D-Bus, so its private fields are never touched.

import Clutter from 'gi://Clutter';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Pango from 'gi://Pango';
import St from 'gi://St';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';

const ROLE_PREFIX = 'appindicator-';
const ITEM_ID_PREFIX = 'openhw-tray-';
const ITEM_PATH = '/StatusNotifierItem';
const ITEM_INTERFACE = 'org.kde.StatusNotifierItem';
const SHOW_DELAY_MS = 350; // about GTK's tooltip delay: passing over the panel doesn't flash tooltips
const GAP = 6;

export default class OpenHWTrayHover extends Extension {
    enable() {
        // Panel button -> {openhw, bus, path, ids}: every appindicator button seen, so each is asked its Id once.
        this._icons = new Map();
        this._cancellable = new Gio.Cancellable();
        this._current = null;
        this._showTimeout = 0;
        this._scanIdle = 0;
        this._tooltipSignal = 0;
        this._menuSignal = null;

        this._title = new St.Label({style_class: 'openhw-tray-tooltip-title'});
        this._body = new St.Label();
        // St labels cut long text off with "…" by default; the explanation should wrap at the box's max-width.
        this._body.clutter_text.ellipsize = Pango.EllipsizeMode.NONE;
        this._body.clutter_text.line_wrap = true;
        this._tooltip = new St.BoxLayout({
            style_class: 'openhw-tray-tooltip',
            orientation: Clutter.Orientation.VERTICAL,
            reactive: false,
            visible: false,
        });
        this._tooltip.add_child(this._title);
        this._tooltip.add_child(this._body);
        Main.uiGroup.add_child(this._tooltip);

        // Icons come and go (OpenHW restarts, icons added in Tray Icons); each lands in one of the panel's boxes.
        this._boxSignals = [Main.panel._leftBox, Main.panel._centerBox, Main.panel._rightBox].map(box =>
            [box, box.connect('child-added', () => this._queueScan())]);
        this._scan();
    }

    disable() {
        this._cancellable.cancel();
        this._hide();
        if (this._scanIdle)
            GLib.source_remove(this._scanIdle);
        for (const [box, id] of this._boxSignals)
            box.disconnect(id);
        for (const [button, icon] of this._icons)
            for (const id of icon.ids)
                button.disconnect(id);
        this._tooltip.destroy();
        this._icons = this._boxSignals = this._tooltip = this._title = this._body = this._cancellable = null;
    }

    _queueScan() {
        this._scanIdle ||= GLib.idle_add(GLib.PRIORITY_DEFAULT_IDLE, () => {
            this._scanIdle = 0;
            this._scan();
            return GLib.SOURCE_REMOVE;
        });
    }

    _scan() {
        for (const [role, button] of Object.entries(Main.panel.statusArea)) {
            if (!role.startsWith(ROLE_PREFIX) || !button || this._icons.has(button))
                continue;
            const [bus, path = ITEM_PATH] = role.slice(ROLE_PREFIX.length).split('@');
            const icon = {openhw: false, bus, path, ids: []};
            this._icons.set(button, icon);
            icon.ids.push(button.connect('destroy', () => this._forget(button)));
            this._getProperty(bus, path, 'Id').then(id => {
                if (this._icons?.get(button) !== icon || typeof id !== 'string' || !id.startsWith(ITEM_ID_PREFIX))
                    return;
                icon.openhw = true;
                icon.ids.push(
                    button.connect('notify::hover', () => this._onHover(button)),
                    // A click opens the icon's menu, which covers the tooltip's place.
                    button.connect('button-press-event', () => {
                        this._hide();
                        return Clutter.EVENT_PROPAGATE;
                    }));
                if (button.hover)
                    this._onHover(button);
            }).catch(() => {}); // not an item that answers (or it just quit): never an OpenHW icon then
        }
    }

    _forget(button) {
        if (this._current === button)
            this._hide();
        this._icons.delete(button);
    }

    _onHover(button) {
        if (!button.hover) {
            if (this._current === button)
                this._hide();
            return;
        }
        if (this._current === button || button.menu?.isOpen)
            return;
        this._hide();
        this._current = button;
        this._showTimeout = GLib.timeout_add(GLib.PRIORITY_DEFAULT, SHOW_DELAY_MS, () => {
            this._showTimeout = 0;
            this._show(button);
            return GLib.SOURCE_REMOVE;
        });
    }

    _show(button) {
        const {bus, path} = this._icons.get(button);
        this._tooltipSignal = Gio.DBus.session.signal_subscribe(bus, ITEM_INTERFACE, 'NewToolTip', path, null,
            Gio.DBusSignalFlags.NONE, () => this._refresh(button));
        // Its menu can also open from the keyboard; the tooltip would sit on top of it.
        if (button.menu)
            this._menuSignal = [button.menu, button.menu.connect('open-state-changed', (_, open) => open && this._hide())];
        this._refresh(button);
    }

    _refresh(button) {
        const {bus, path} = this._icons.get(button);
        this._getProperty(bus, path, 'ToolTip').then(([, , title, body]) => {
            if (this._current !== button)
                return;
            if (!button.hover || button.menu?.isOpen) {
                this._hide();
                return;
            }
            if (!title && !body) {
                this._tooltip.hide();
                return;
            }
            // Plain text, never markup: the SNI spec allows markup in the body, but OpenHW's sensor names are data.
            this._title.text = title;
            this._title.visible = title.length > 0;
            this._body.text = body;
            this._body.visible = body.length > 0;
            this._tooltip.show();
            Main.uiGroup.set_child_above_sibling(this._tooltip, null);
            this._place(button);
        }).catch(() => {});
    }

    // Centered under the icon, kept on the icon's monitor.
    _place(button) {
        const [x, y] = button.get_transformed_position();
        const [width, height] = button.get_transformed_size();
        const monitor = Main.layoutManager.findMonitorForActor(button) ?? Main.layoutManager.primaryMonitor;
        const tipWidth = this._tooltip.width;
        const left = Math.clamp(Math.round(x + (width - tipWidth) / 2), monitor.x, monitor.x + monitor.width - tipWidth);
        this._tooltip.set_position(left, Math.round(y + height + GAP));
    }

    _hide() {
        if (this._showTimeout) {
            GLib.source_remove(this._showTimeout);
            this._showTimeout = 0;
        }
        if (this._tooltipSignal) {
            Gio.DBus.session.signal_unsubscribe(this._tooltipSignal);
            this._tooltipSignal = 0;
        }
        if (this._menuSignal) {
            const [menu, id] = this._menuSignal;
            menu.disconnect(id);
            this._menuSignal = null;
        }
        this._current = null;
        this._tooltip?.hide();
    }

    _getProperty(bus, path, name) {
        return new Promise((resolve, reject) => {
            Gio.DBus.session.call(bus, path, 'org.freedesktop.DBus.Properties', 'Get',
                new GLib.Variant('(ss)', [ITEM_INTERFACE, name]), new GLib.VariantType('(v)'),
                Gio.DBusCallFlags.NONE, 2000, this._cancellable, (connection, result) => {
                    try {
                        resolve(connection.call_finish(result).recursiveUnpack()[0]);
                    } catch (e) {
                        reject(e);
                    }
                });
        });
    }
}
