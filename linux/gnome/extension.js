// Widgets for OPNsense, GNOME Shell: a top bar indicator fed by the opnsense-widgetsd user service
// (the same daemon that serves the KDE Plasma widgets).
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';

import {Indicator} from './indicator.js';

export default class OPNsenseWidgetsExtension extends Extension {
    enable() {
        this._indicator = new Indicator(this);
        Main.panel.addToStatusArea(this.uuid, this._indicator);
    }

    disable() {
        this._indicator?.destroy();
        this._indicator = null;
    }
}
