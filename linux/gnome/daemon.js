// Connection to opnsense-widgetsd, shared by the panel indicator and the preferences window.
// Speaks the same WebSocket protocol as the widget pages (see Shared/Web/widget.js):
//   to the daemon:   {type:"ready"}, {type:"refresh"}, {type:"action", verb, data}
//   from the daemon: {type:"data", view, payload}, {type:"navigate", path}
// Only GLib and Soup here, so the preferences process (GTK) can import it too.
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Soup from 'gi://Soup?version=3.0';

export const DEFINITIONS = {
    dhcp: {id: 'DhcpLeases_Widget', path: 'DhcpLeases/Web/index.html'},
    traffic: {id: 'Traffic_Widget', path: 'Traffic/Web/index.html'},
    firewall: {id: 'Firewall_Widget', path: 'Firewall/Web/index.html'},
};

export const SETTINGS_PATH = 'Shared/Web/settings.html';

const RETRY_SECONDS = 3;
const READY_SECONDS = 3;

/**
 * One widget instance on the daemon. Reconnects on its own; the daemon polls the router only
 * while a channel is open.
 *
 * onData(view, payload): view is "data", "status" or "settings".
 * onState(state): "connecting", "open" or "offline" (daemon not reachable); "stopped" after stop().
 * followNavigate: when the daemon switches the widget to another view it sends the new page
 * path; a channel that follows reconnects with it, like a page reloading.
 */
export class WidgetChannel {
    constructor({port, definition, widgetId, path, followNavigate = true, onData, onState}) {
        this._port = port;
        this._definition = definition;
        this._widgetId = widgetId;
        this._path = path;
        this._followNavigate = followNavigate;
        this._onData = onData ?? (() => {});
        this._onState = onState ?? (() => {});

        this._session = new Soup.Session({timeout: 10});
        this._connection = null;
        this._connectionSignals = [];
        this._cancellable = null;
        this._retryId = 0;
        this._readyId = 0;
        this._gotData = false;
        this._stopped = true;
        this.state = 'stopped';
    }

    get running() {
        return !this._stopped;
    }

    start() {
        if (!this._stopped)
            return;
        this._stopped = false;
        this._connect();
    }

    stop() {
        if (this._stopped)
            return;
        this._stopped = true;
        this._clearTimers();
        this._cancellable?.cancel();
        this._cancellable = null;
        this._dropConnection();
        this.state = 'stopped';
    }

    setPort(port) {
        if (port === this._port)
            return;
        this._port = port;
        if (!this._stopped) {
            this._dropConnection();
            this._connect();
        }
    }

    refresh() {
        this.send({type: 'refresh'});
    }

    action(verb, data = {}) {
        this.send({type: 'action', verb, data});
    }

    send(message) {
        if (this._connection?.get_state() !== Soup.WebsocketState.OPEN)
            return false;
        this._connection.send_text(JSON.stringify(message));
        return true;
    }

    _connect() {
        this._clearTimers();
        this._setState('connecting');

        const query = [
            `def=${encodeURIComponent(this._definition)}`,
            `id=${encodeURIComponent(this._widgetId)}`,
            `path=${encodeURIComponent(`/${this._path}`)}`,
        ].join('&');
        // http, not ws: Soup upgrades the request itself. No Origin header, like a page on the
        // daemon's own origin would send; the daemon rejects any other origin.
        const message = Soup.Message.new('GET', `http://127.0.0.1:${this._port}/ws?${query}`);
        const cancellable = new Gio.Cancellable();
        this._cancellable = cancellable;

        this._session.websocket_connect_async(message, null, null, GLib.PRIORITY_DEFAULT, cancellable,
            (session, result) => {
                if (cancellable.is_cancelled())
                    return;
                let connection;
                try {
                    connection = session.websocket_connect_finish(result);
                } catch {
                    this._setState('offline');
                    this._retry();
                    return;
                }
                this._attach(connection);
            });
    }

    _attach(connection) {
        this._connection = connection;
        // Traffic history can be large; Soup's default limit is 128 KiB.
        connection.max_incoming_payload_size = 16 * 1024 * 1024;
        connection.keepalive_interval = 30;
        this._connectionSignals = [
            connection.connect('message', (_c, type, bytes) => {
                if (type === Soup.WebsocketDataType.TEXT)
                    this._onMessage(new TextDecoder().decode(bytes.toArray()));
            }),
            connection.connect('closed', () => {
                this._dropConnection();
                if (!this._stopped) {
                    this._setState('offline');
                    this._retry();
                }
            }),
        ];

        this._gotData = false;
        this._setState('open');
        this._sayReady();
    }

    // Like a page: "ready" until the first data arrives (the daemon may still be fetching).
    _sayReady() {
        this.send({type: 'ready'});
        this._readyId = GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, READY_SECONDS, () => {
            if (this._gotData || this._stopped) {
                this._readyId = 0;
                return GLib.SOURCE_REMOVE;
            }
            this.send({type: 'ready'});
            return GLib.SOURCE_CONTINUE;
        });
    }

    _onMessage(text) {
        let message;
        try {
            message = JSON.parse(text);
        } catch {
            return;
        }

        if (message?.type === 'navigate' && typeof message.path === 'string') {
            if (!this._followNavigate)
                return;
            this._path = message.path.replace(/^\/+/, '');
            this._dropConnection();
            this._connect();
            return;
        }

        if (message?.type === 'data') {
            this._gotData = true;
            this._onData(message.view, message.payload ?? {});
        }
    }

    _retry() {
        if (this._retryId || this._stopped)
            return;
        this._retryId = GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, RETRY_SECONDS, () => {
            this._retryId = 0;
            if (!this._stopped)
                this._connect();
            return GLib.SOURCE_REMOVE;
        });
    }

    _dropConnection() {
        if (this._readyId) {
            GLib.source_remove(this._readyId);
            this._readyId = 0;
        }
        const connection = this._connection;
        if (!connection)
            return;
        this._connectionSignals.forEach(id => connection.disconnect(id));
        this._connectionSignals = [];
        this._connection = null;
        if (connection.get_state() === Soup.WebsocketState.OPEN)
            connection.close(Soup.WebsocketCloseCode.NORMAL, null);
    }

    _clearTimers() {
        for (const name of ['_retryId', '_readyId']) {
            if (this[name]) {
                GLib.source_remove(this[name]);
                this[name] = 0;
            }
        }
    }

    _setState(state) {
        if (this.state === state)
            return;
        this.state = state;
        this._onState(state);
    }
}
