// Top bar button and its menu: devices, traffic and firewall from opnsense-widgetsd, plus a
// notification when a new device joins the network.
import Clutter from 'gi://Clutter';
import GLib from 'gi://GLib';
import GObject from 'gi://GObject';
import Pango from 'gi://Pango';
import St from 'gi://St';

import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as MessageTray from 'resource:///org/gnome/shell/ui/messageTray.js';
import * as PanelMenu from 'resource:///org/gnome/shell/ui/panelMenu.js';
import * as PopupMenu from 'resource:///org/gnome/shell/ui/popupMenu.js';
import * as Config from 'resource:///org/gnome/shell/misc/config.js';

import {DEFINITIONS, WidgetChannel} from './daemon.js';
import * as fmt from './format.js';

const SHELL_MAJOR = parseInt(Config.PACKAGE_VERSION, 10);
const HOUR_MS = 3600 * 1000;
const MAX_DEVICES = 6;
const MAX_INTERFACES = 5;
const MAX_RULES = 4;
const SPARK_WINDOW_MS = 5 * 60 * 1000;
const SPARK_MIN_MS = 60 * 1000;
const SPARK_GAP_MS = 90 * 1000;
const DIM = 150; // opacity for secondary text; works on light and dark shell themes

// Same hues as the widget pages (widget.css).
const COLOR_DOWN = [0x35 / 255, 0x84 / 255, 0xe4 / 255];
const COLOR_UP = [0x2e / 255, 0xc2 / 255, 0x7e / 255];

// St.BoxLayout's "vertical" became "orientation" in GNOME 48.
function box(params = {}, vertical = false) {
    const props = {...params};
    if (vertical) {
        if (SHELL_MAJOR >= 48)
            props.orientation = Clutter.Orientation.VERTICAL;
        else
            props.vertical = true;
    }
    return new St.BoxLayout(props);
}

function text(value, styleClass = '', params = {}) {
    const label = new St.Label({
        text: value ?? '',
        style_class: styleClass,
        y_align: Clutter.ActorAlign.CENTER,
        ...params,
    });
    label.clutter_text.ellipsize = Pango.EllipsizeMode.END;
    return label;
}

function dim(actor) {
    actor.opacity = DIM;
    return actor;
}

function spacer() {
    return new St.Widget({x_expand: true});
}

const isNew = (lease, now, newMs) => lease.firstSeen != null && now - lease.firstSeen < newMs;
const isActive = (lease, now, activeMs) => lease.start != null && now - lease.start < activeMs;

function latestSample(iface) {
    const s = iface?.samples?.[iface.samples.length - 1];
    return s ? {down: s[1], up: s[2]} : null;
}

function wanOf(traffic) {
    const ifs = traffic?.interfaces ?? [];
    return ifs.find(i => i.isWan) ?? ifs[0] ?? null;
}

export const Indicator = GObject.registerClass(
class OPNsenseIndicator extends PanelMenu.Button {
    _init(extension) {
        super._init(0.5, 'OPNsense', false);

        this._extension = extension;
        this._settings = extension.getSettings();
        this._data = {dhcp: null, traffic: null, firewall: null};
        this._status = {dhcp: null, traffic: null, firewall: null};
        this._knownDevices = null;
        this._source = null;

        // Panel: icon, a dot while there are new devices, and the chosen reading.
        const panelBox = box({style_class: 'panel-status-menu-box'});
        this._icon = new St.Icon({icon_name: 'network-wired-symbolic', style_class: 'system-status-icon'});
        this._newDot = new St.Widget({
            style_class: 'opnsense-new-dot',
            visible: false,
            y_align: Clutter.ActorAlign.CENTER,
        });
        this._panelLabel = new St.Label({
            text: '',
            style_class: 'opnsense-panel-label',
            y_align: Clutter.ActorAlign.CENTER,
            visible: false,
        });
        panelBox.add_child(this._icon);
        panelBox.add_child(this._newDot);
        panelBox.add_child(this._panelLabel);
        this.add_child(panelBox);

        // Menu: the sections, then the actions. The sections sit in a plain menu section, not a
        // menu item (a non-activatable item is drawn as disabled), and scroll when the screen
        // is too short, so the actions stay visible.
        this._content = box({style_class: 'opnsense-menu'}, true);
        const scroll = new St.ScrollView({
            style_class: 'opnsense-scroll',
            hscrollbar_policy: St.PolicyType.NEVER,
            vscrollbar_policy: St.PolicyType.AUTOMATIC,
            overlay_scrollbars: true,
            y_expand: true,
        });
        scroll.set_child(this._content);
        const section = new PopupMenu.PopupMenuSection();
        section.actor.add_child(scroll);
        this.menu.addMenuItem(section);
        this.menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
        this.menu.addAction('Refresh', () => this._refreshAll(), 'view-refresh-symbolic');
        this.menu.addAction('Settings…', () => this._extension.openPreferences(), 'preferences-system-symbolic');
        this.menu.connect('open-state-changed', (_menu, open) => {
            this._updateChannels();
            if (open)
                this._render();
        });

        const port = this._settings.get_int('port');
        this._channels = {};
        for (const [key, definition] of Object.entries(DEFINITIONS)) {
            this._channels[key] = new WidgetChannel({
                port,
                definition: definition.id,
                widgetId: `gnome-${key}`,
                path: definition.path,
                onData: (view, payload) => this._onData(key, view, payload),
                onState: () => this._changed(),
            });
        }

        this._settingsSignals = [
            this._settings.connect('changed::port', () => {
                const newPort = this._settings.get_int('port');
                Object.values(this._channels).forEach(c => c.setPort(newPort));
            }),
            this._settings.connect('changed::panel-label', () => {
                this._updateChannels();
                this._updatePanel();
            }),
            // The preferences window saved a connection: fetch now instead of at the next interval.
            this._settings.connect('changed::config-saved', () => this._refreshAll()),
        ];

        // Relative times ("5m") in the menu age while it stays open.
        this._tickId = GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, 60, () => {
            if (this.menu.isOpen)
                this._render();
            return GLib.SOURCE_CONTINUE;
        });

        this._updateChannels();
        this._updatePanel();
    }

    /**
     * The daemon polls the router while a channel is open, and Traffic and Firewall poll every
     * second. Devices stay connected (count, new-device notifications); Traffic only while the
     * menu is open or the panel shows throughput; Firewall only while the menu is open.
     */
    _updateChannels() {
        const open = this.menu.isOpen;
        const wanted = {
            dhcp: true,
            traffic: open || this._settings.get_string('panel-label') === 'throughput',
            firewall: open,
        };
        for (const [key, channel] of Object.entries(this._channels)) {
            if (wanted[key])
                channel.start();
            else
                channel.stop();
        }
    }

    destroy() {
        if (this._tickId) {
            GLib.source_remove(this._tickId);
            this._tickId = 0;
        }
        this._settingsSignals.forEach(id => this._settings.disconnect(id));
        this._settingsSignals = [];
        Object.values(this._channels).forEach(c => c.stop());
        this._source?.destroy();
        this._source = null;
        super.destroy();
    }

    _refreshAll() {
        Object.values(this._channels).forEach(c => c.refresh());
    }

    _onData(key, view, payload) {
        if (view === 'data') {
            this._data[key] = payload;
            this._status[key] = null;
            if (key === 'dhcp')
                this._checkNewDevices(payload);
        } else if (view === 'status') {
            this._status[key] = payload;
        }
        this._changed();
    }

    _changed() {
        this._updatePanel();
        if (this.menu.isOpen)
            this._render();
    }

    /** "offline" (daemon down), "unconfigured", "error" (some widget failing) or "ok". */
    _overall() {
        const states = Object.values(this._channels).map(c => c.state);
        if (!states.includes('open') && states.includes('offline'))
            return 'offline';
        const statuses = Object.values(this._status);
        if (statuses.some(s => s?.kind === 'unconfigured'))
            return 'unconfigured';
        if (statuses.some(s => s?.kind === 'error'))
            return 'error';
        return 'ok';
    }

    _updatePanel() {
        const overall = this._overall();
        this._icon.icon_name = {
            offline: 'network-wired-disconnected-symbolic',
            unconfigured: 'network-wired-disconnected-symbolic',
            error: 'network-error-symbolic',
        }[overall] ?? 'network-wired-symbolic';

        let label = '';
        let newCount = 0;
        const dhcp = this._data.dhcp;
        if (overall === 'ok' || overall === 'error') {
            const mode = this._settings.get_string('panel-label');
            if (mode === 'devices' && dhcp && !this._status.dhcp) {
                label = String(dhcp.total ?? dhcp.leases?.length ?? 0);
            } else if (mode === 'throughput' && !this._status.traffic) {
                const now = latestSample(wanOf(this._data.traffic));
                if (now)
                    label = `↓${fmt.bitsShort(now.down)} ↑${fmt.bitsShort(now.up)}`;
            }
            if (dhcp) {
                const at = Date.now();
                const newMs = dhcp.newWindowMs || 24 * HOUR_MS;
                newCount = (dhcp.leases ?? []).filter(l => isNew(l, at, newMs)).length;
            }
        }

        this._panelLabel.text = label;
        this._panelLabel.visible = label.length > 0;
        this._newDot.visible = newCount > 0;
    }

    // ─── Menu ───

    _render() {
        this._content.destroy_all_children();
        const overall = this._overall();

        if (overall === 'offline') {
            this._content.add_child(this._message('Widget service not running',
                'Start it with: systemctl --user start opnsense-widgetsd'));
            return;
        }
        if (overall === 'unconfigured') {
            this._content.add_child(this._message('Not connected to OPNsense',
                'Open Settings to add the router URL and API key.'));
            return;
        }

        this._content.add_child(this._dhcpSection());
        this._content.add_child(this._trafficSection());
        this._content.add_child(this._firewallSection());

        const updated = Math.max(0, ...Object.values(this._data).map(d => d?.updatedAt ?? 0));
        if (updated)
            this._content.add_child(dim(text(`Updated ${fmt.time(updated)}`, 'opnsense-small opnsense-footer')));
    }

    _message(title, body) {
        const section = box({style_class: 'opnsense-message'}, true);
        section.add_child(text(title, 'opnsense-message-title'));
        const detail = text(body, 'opnsense-small');
        detail.clutter_text.ellipsize = Pango.EllipsizeMode.NONE;
        detail.clutter_text.line_wrap = true;
        section.add_child(dim(detail));
        return section;
    }

    _section(caption) {
        const section = box({style_class: 'opnsense-section'}, true);
        section.add_child(dim(text(caption.toUpperCase(), 'opnsense-caption')));
        return section;
    }

    /** Error text or "Loading…" for a widget without data; null when it has data. */
    _placeholder(key) {
        const status = this._status[key];
        if (status?.kind === 'error')
            return this._message('Can\'t reach OPNsense', status.message ?? 'Unknown error');
        if (!this._data[key])
            return dim(text('Loading…', 'opnsense-small'));
        return null;
    }

    _dhcpSection() {
        const section = this._section('Devices');
        const placeholder = this._placeholder('dhcp');
        if (placeholder) {
            section.add_child(placeholder);
            return section;
        }

        const data = this._data.dhcp;
        const now = Date.now();
        const leases = data.leases ?? [];
        const newMs = data.newWindowMs || 24 * HOUR_MS;
        const activeMs = data.activeWindowMs || HOUR_MS;
        const trackingSince = data.trackingSince ?? null;
        const total = data.total ?? leases.length;

        let newCount = 0, activeCount = 0, reservedCount = 0;
        for (const lease of leases) {
            if (lease.reserved)
                reservedCount++;
            if (isNew(lease, now, newMs))
                newCount++;
            if (isActive(lease, now, activeMs))
                activeCount++;
        }

        const head = box({style_class: 'opnsense-head'});
        head.add_child(text(String(total), 'opnsense-hero'));
        head.add_child(dim(text(total === 1 ? 'device' : 'devices', 'opnsense-hero-unit')));
        head.add_child(spacer());
        const stats = box({style_class: 'opnsense-stats'});
        for (const [count, name] of [[newCount, 'new'], [activeCount, 'active'], [reservedCount, 'reserved']]) {
            const stat = box({style_class: 'opnsense-stat'});
            stat.add_child(new St.Widget({style_class: `opnsense-dot opnsense-${name}`, y_align: Clutter.ActorAlign.CENTER}));
            stat.add_child(text(String(count), 'opnsense-stat-num'));
            stat.add_child(dim(text(name, 'opnsense-small')));
            stats.add_child(stat);
        }
        head.add_child(stats);
        section.add_child(head);

        for (const lease of leases.slice(0, MAX_DEVICES)) {
            const status = lease.reserved ? 'reserved'
                : isNew(lease, now, newMs) ? 'new'
                    : isActive(lease, now, activeMs) ? 'active'
                        : 'stale';

            const row = box({style_class: 'opnsense-device'});
            row.add_child(new St.Widget({style_class: `opnsense-bar opnsense-${status}`}));

            const body = box({x_expand: true}, true);
            const top = box({style_class: 'opnsense-row'});
            top.add_child(text(lease.host || lease.ip, 'opnsense-device-name', {x_expand: true}));
            if (lease.reserved)
                top.add_child(text('RSV', 'opnsense-badge'));
            const joined = lease.firstSeen != null
                ? fmt.relative(lease.firstSeen, now)
                : trackingSince != null && now - trackingSince >= HOUR_MS
                    ? `${fmt.relative(trackingSince, now)}+`
                    : '';
            top.add_child(dim(text(joined, 'opnsense-small')));
            body.add_child(top);

            const meta = [lease.host ? lease.ip : null, lease.vendor].filter(Boolean).join(' · ');
            if (meta)
                body.add_child(dim(text(meta, 'opnsense-small')));
            row.add_child(body);
            section.add_child(row);
        }

        if (leases.length > MAX_DEVICES)
            section.add_child(dim(text(`and ${leases.length - MAX_DEVICES} more`, 'opnsense-small')));
        return section;
    }

    _trafficSection() {
        const section = this._section('Traffic');
        const placeholder = this._placeholder('traffic');
        if (placeholder) {
            section.add_child(placeholder);
            return section;
        }

        const interfaces = this._data.traffic.interfaces ?? [];
        const wan = wanOf(this._data.traffic);
        if (!wan) {
            section.add_child(dim(text('No interfaces', 'opnsense-small')));
            return section;
        }

        const now = latestSample(wan) ?? {down: 0, up: 0};
        const head = box({style_class: 'opnsense-head'});
        head.add_child(text(`↓ ${fmt.bits(now.down)}`, 'opnsense-reading opnsense-down'));
        head.add_child(text(`↑ ${fmt.bits(now.up)}`, 'opnsense-reading opnsense-up'));
        head.add_child(spacer());
        head.add_child(dim(text(wan.name, 'opnsense-small')));
        section.add_child(head);
        section.add_child(this._sparkline(wan.samples ?? []));

        for (const iface of interfaces.filter(i => i !== wan).slice(0, MAX_INTERFACES)) {
            const reading = latestSample(iface) ?? {down: 0, up: 0};
            const row = box({style_class: 'opnsense-row'});
            row.add_child(text(iface.name, 'opnsense-iface', {x_expand: true}));
            row.add_child(text(`↓ ${fmt.bits(reading.down)}`, 'opnsense-small opnsense-rate opnsense-down'));
            row.add_child(text(`↑ ${fmt.bits(reading.up)}`, 'opnsense-small opnsense-rate opnsense-up'));
            section.add_child(row);
        }
        return section;
    }

    /** Download and upload over the last few minutes; runs with gaps (polling paused) are split. */
    _sparkline(samples) {
        const area = new St.DrawingArea({style_class: 'opnsense-spark', x_expand: true});
        area.connect('repaint', () => {
            const cr = area.get_context();
            const [width, height] = area.get_surface_size();
            const end = samples.length ? samples[samples.length - 1][0] : 0;
            const visible = samples.filter(s => s[0] >= end - SPARK_WINDOW_MS);
            const max = Math.max(1, ...visible.map(s => Math.max(s[1], s[2])));
            // History restarts while nothing watches Traffic: span what there is, up to the window.
            const span = Math.max(SPARK_MIN_MS, end - (visible[0]?.[0] ?? end));
            const x = t => (1 - (end - t) / span) * (width - 2) + 1;
            const y = v => height - 2 - (v / max) * (height - 4);

            cr.setLineWidth(1.5);
            for (const [index, color] of [[1, COLOR_DOWN], [2, COLOR_UP]]) {
                cr.setSourceRGBA(...color, 1);
                let previous = null;
                for (const s of visible) {
                    if (previous && s[0] - previous[0] <= SPARK_GAP_MS)
                        cr.lineTo(x(s[0]), y(s[index]));
                    else
                        cr.moveTo(x(s[0]), y(s[index]));
                    previous = s;
                }
                cr.stroke();
            }
            cr.$dispose();
        });
        return area;
    }

    _firewallSection() {
        const section = this._section('Firewall');
        const placeholder = this._placeholder('firewall');
        if (placeholder) {
            section.add_child(placeholder);
            return section;
        }

        const data = this._data.firewall;
        const head = box({style_class: 'opnsense-head'});
        head.add_child(text(data.rate != null ? fmt.compact(data.rate) : '…', 'opnsense-hero'));
        head.add_child(dim(text('evals/s', 'opnsense-hero-unit')));
        head.add_child(spacer());
        const stats = box({style_class: 'opnsense-stats'});
        stats.add_child(text(String(data.ruleCount ?? 0), 'opnsense-stat-num'));
        stats.add_child(dim(text('rules', 'opnsense-small')));
        stats.add_child(text(fmt.compact(data.totals?.states ?? 0), 'opnsense-stat-num'));
        stats.add_child(dim(text('states', 'opnsense-small')));
        head.add_child(stats);
        section.add_child(head);

        // Busiest right now when rates are known (from the second reading on), else all-time.
        const hasRates = (data.rules ?? []).some(r => r.rate != null);
        const rules = [...(data.rules ?? [])]
            .sort((a, b) => hasRates
                ? (b.rate ?? -1) - (a.rate ?? -1)
                : (b.evaluations ?? 0) - (a.evaluations ?? 0))
            .slice(0, MAX_RULES);
        for (const rule of rules) {
            const row = box({style_class: 'opnsense-row'});
            row.add_child(text(rule.label, 'opnsense-rule', {x_expand: true}));
            row.add_child(dim(text(hasRates
                ? `${fmt.compact(rule.rate ?? 0)}/s`
                : fmt.compact(rule.evaluations), 'opnsense-small opnsense-rate')));
            section.add_child(row);
        }
        return section;
    }

    // ─── New devices ───

    _checkNewDevices(data) {
        const now = Date.now();
        const newMs = data.newWindowMs || 24 * HOUR_MS;
        const leases = data.leases ?? [];
        const keyOf = lease => (lease.mac || lease.ip || '').toLowerCase();

        // The first reading after login only sets the baseline.
        if (!this._knownDevices) {
            this._knownDevices = new Set(leases.map(keyOf));
            return;
        }

        const arrivals = leases.filter(l => !this._knownDevices.has(keyOf(l)) && isNew(l, now, newMs));
        leases.forEach(l => this._knownDevices.add(keyOf(l)));
        if (!arrivals.length || !this._settings.get_boolean('notify-new-devices'))
            return;

        const describe = l => [l.host || l.ip, l.host ? l.ip : null, l.vendor].filter(Boolean).join(' · ');
        if (arrivals.length === 1)
            this._notify('New device on your network', describe(arrivals[0]));
        else
            this._notify(`${arrivals.length} new devices on your network`, arrivals.slice(0, 5).map(describe).join('\n'));
    }

    _notify(title, body) {
        if (!this._source) {
            this._source = new MessageTray.Source({title: 'OPNsense', iconName: 'network-wired-symbolic'});
            this._source.connect('destroy', () => {
                this._source = null;
            });
            Main.messageTray.add(this._source);
        }
        const notification = new MessageTray.Notification({source: this._source, title, body});
        this._source.addNotification(notification);
    }
});
