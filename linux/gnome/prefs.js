// Preferences: the OPNsense connection (stored by the daemon, the secret in the keyring) and how
// the top bar button looks. The connection page talks to opnsense-widgetsd with the same actions
// as the widgets' settings page (Shared/Web/settings.html).
import Adw from 'gi://Adw';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Gtk from 'gi://Gtk';

import {ExtensionPreferences} from 'resource:///org/gnome/Shell/Extensions/js/extensions/prefs.js';

import {DEFINITIONS, SETTINGS_PATH, WidgetChannel} from './daemon.js';

const PANEL_LABELS = [
    ['devices', 'Device count'],
    ['throughput', 'WAN throughput'],
    ['none', 'Icon only'],
];

export default class OPNsenseWidgetsPreferences extends ExtensionPreferences {
    fillPreferencesWindow(window) {
        const settings = this.getSettings();
        window._settings = settings;
        window.set_default_size(600, 660);

        window.add(this._connectionPage(window, settings));
        window.add(this._panelPage(settings));
    }

    _connectionPage(window, settings) {
        const page = new Adw.PreferencesPage({title: 'Connection', icon_name: 'network-wired-symbolic'});

        const group = new Adw.PreferencesGroup({
            title: 'OPNsense router',
            description: 'Create an API key on the router under System > Access > Users. ' +
                'The secret is kept in your keyring.',
        });
        const url = new Adw.EntryRow({title: 'Router URL (https://192.168.1.1)'});
        const key = new Adw.EntryRow({title: 'API key'});
        const secret = new Adw.PasswordEntryRow({title: 'API secret'});
        const selfSigned = new Adw.SwitchRow({
            title: 'Allow self-signed certificate',
            subtitle: 'Most home routers use one',
            active: true,
        });
        const interval = new Adw.SpinRow({
            title: 'Refresh every',
            subtitle: 'Seconds',
            adjustment: new Gtk.Adjustment({lower: 10, upper: 300, step_increment: 5, page_increment: 30, value: 30}),
        });
        [url, key, secret, selfSigned, interval].forEach(row => group.add(row));
        page.add(group);

        const resultGroup = new Adw.PreferencesGroup();
        const icon = new Gtk.Image({icon_name: 'content-loading-symbolic'});
        const result = new Adw.ActionRow({title: 'Connecting to the widget service…', title_lines: 3});
        result.add_prefix(icon);
        resultGroup.add(result);

        const buttons = new Gtk.Box({spacing: 8, halign: Gtk.Align.END, margin_top: 12});
        const importButton = new Gtk.Button({label: 'Import Key from Downloads'});
        const saveButton = new Gtk.Button({label: 'Save and Test', css_classes: ['suggested-action']});
        buttons.append(importButton);
        buttons.append(saveButton);
        resultGroup.add(buttons);
        page.add(resultGroup);

        // state: true (worked), false (failed), 'info', or null (busy).
        const setResult = (message, state) => {
            result.title = GLib.markup_escape_text(message, -1);
            icon.icon_name = {
                true: 'emblem-ok-symbolic',
                false: 'dialog-warning-symbolic',
                info: 'dialog-information-symbolic',
            }[state] ?? 'content-loading-symbolic';
        };
        const setEnabled = enabled => [group, importButton, saveButton].forEach(w => (w.sensitive = enabled));
        setEnabled(false);

        let filled = false;
        let testing = false;
        const channel = new WidgetChannel({
            port: settings.get_int('port'),
            definition: DEFINITIONS.dhcp.id,
            widgetId: 'gnome-prefs',
            path: SETTINGS_PATH,
            followNavigate: false, // stay on the form; the daemon only switches views for pages
            onState: state => {
                if (state === 'open') {
                    // Puts this widget in settings mode; the daemon answers with the saved values.
                    channel.action('openSettings');
                } else if (state === 'offline') {
                    setEnabled(false);
                    setResult('Widget service not running. Start it with: systemctl --user start opnsense-widgetsd', false);
                }
            },
            onData: (view, payload) => {
                if (view !== 'settings')
                    return;
                setEnabled(true);

                const imported = (payload.testResult ?? '').startsWith('Imported');
                if (!filled || imported) {
                    if (!filled) {
                        url.text = payload.hostUrl ?? '';
                        selfSigned.active = payload.allowSelfSigned ?? true;
                        interval.value = payload.refreshInterval || 30;
                    }
                    key.text = payload.apiKey ?? '';
                    secret.text = '';
                    filled = true;
                }
                secret.title = payload.hasSecret ? 'API secret (saved; leave empty to keep it)' : 'API secret';

                if (payload.testResult)
                    setResult(payload.testResult, payload.testOk ?? 'info');
                else if (!testing)
                    setResult(payload.hasSecret ? 'Saved connection loaded.' : 'Enter the router URL and API key.', 'info');

                if (testing && payload.testResult) {
                    testing = false;
                    // Lets the top bar fetch right away instead of at its next interval.
                    if (payload.testOk)
                        settings.set_int64('config-saved', GLib.get_real_time());
                }
            },
        });

        saveButton.connect('clicked', () => {
            testing = true;
            setResult('Saving and testing…', null);
            channel.action('testConnection', {
                hostUrl: url.text.trim(),
                apiKey: key.text.trim(),
                apiSecret: secret.text.trim(),
                allowSelfSigned: selfSigned.active ? 'true' : 'false',
                refreshInterval: String(interval.value),
            });
        });
        importButton.connect('clicked', () => {
            setResult('Looking for *_apikey.txt in Downloads…', null);
            channel.action('importKeyFile');
        });

        channel.start();
        window.connect('close-request', () => {
            channel.action('cancelConfig');
            channel.stop();
            return false;
        });
        return page;
    }

    _panelPage(settings) {
        const page = new Adw.PreferencesPage({title: 'Top Bar', icon_name: 'preferences-desktop-display-symbolic'});

        const group = new Adw.PreferencesGroup({title: 'Top bar button'});
        const labels = new Adw.ComboRow({
            title: 'Show next to the icon',
            model: Gtk.StringList.new(PANEL_LABELS.map(([, name]) => name)),
        });
        const current = PANEL_LABELS.findIndex(([id]) => id === settings.get_string('panel-label'));
        labels.selected = Math.max(0, current);
        labels.connect('notify::selected', () =>
            settings.set_string('panel-label', PANEL_LABELS[labels.selected][0]));
        group.add(labels);

        const notify = new Adw.SwitchRow({
            title: 'Notify about new devices',
            subtitle: 'When a device joins the network for the first time',
        });
        settings.bind('notify-new-devices', notify, 'active', Gio.SettingsBindFlags.DEFAULT);
        group.add(notify);
        page.add(group);

        const service = new Adw.PreferencesGroup({
            title: 'Widget service',
            description: 'Only change this if you changed OPNSENSE_WIDGETS_PORT for opnsense-widgetsd.',
        });
        const port = new Adw.SpinRow({
            title: 'Port',
            adjustment: new Gtk.Adjustment({lower: 1024, upper: 65535, step_increment: 1, page_increment: 10}),
        });
        settings.bind('port', port, 'value', Gio.SettingsBindFlags.DEFAULT);
        service.add(port);
        page.add(service);

        return page;
    }
}
