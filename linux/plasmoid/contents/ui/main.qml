// OPNsense widget for KDE Plasma 6. The widget itself is the same HTML page as on Windows, served by
// the opnsense-widgetsd user service on 127.0.0.1. install.sh stamps one package per widget from this
// template, replacing the @...@ placeholders.
import QtQuick
import QtQuick.Layouts
import QtWebEngine
import org.kde.plasma.plasmoid
import org.kde.plasma.core as PlasmaCore
import org.kde.kirigami as Kirigami

PlasmoidItem {
    id: root

    readonly property string definitionId: "@DEFINITION@"
    readonly property int port: Plasmoid.configuration.port
    readonly property bool darkTheme:
        Kirigami.ColorUtils.brightnessForColor(Kirigami.Theme.backgroundColor) === Kirigami.ColorUtils.Dark
    // The daemon redirects /widget to the page for the widget's current view (data, settings or status).
    readonly property string pageUrl: "http://127.0.0.1:" + port + "/widget?def=" + definitionId
        + "&id=kde-" + Plasmoid.id + "&host=kde&theme=" + (darkTheme ? "dark" : "light")

    Plasmoid.backgroundHints: PlasmaCore.Types.DefaultBackground | PlasmaCore.Types.ConfigurableBackground

    // On the desktop show the widget itself; in a panel show the icon with the widget as its popup.
    preferredRepresentation: Plasmoid.formFactor === PlasmaCore.Types.Planar
        ? fullRepresentation : compactRepresentation

    Plasmoid.contextualActions: [
        PlasmaCore.Action {
            text: i18n("OPNsense Connection…")
            icon.name: "network-connect"
            onTriggered: {
                root.expanded = true;
                if (root.fullRepresentationItem)
                    root.fullRepresentationItem.openSettings();
            }
        },
        PlasmaCore.Action {
            text: i18n("Refresh")
            icon.name: "view-refresh"
            onTriggered: {
                if (root.fullRepresentationItem)
                    root.fullRepresentationItem.reload();
            }
        }
    ]

    fullRepresentation: Item {
        id: full

        // Same canvas as the Windows medium size; the page adapts to any size.
        Layout.preferredWidth: 302
        Layout.preferredHeight: 306
        Layout.minimumWidth: 220
        Layout.minimumHeight: 130

        property bool unreachable: false

        function openSettings() {
            // Same verb as the status page's Settings button; the daemon switches the page.
            web.runJavaScript("window.Widget && Widget.action('openSettings')");
        }

        function reload() {
            unreachable = false;
            web.url = "about:blank";
            web.url = root.pageUrl;
        }

        WebEngineView {
            id: web
            anchors.fill: parent
            visible: !full.unreachable
            url: root.pageUrl
            backgroundColor: "transparent"
            settings.showScrollBars: false
            settings.javascriptCanAccessClipboard: false
            settings.localContentCanAccessRemoteUrls: false

            onLoadingChanged: loadingInfo => {
                if (loadingInfo.status === WebEngineView.LoadFailedStatus) {
                    full.unreachable = true;
                    retry.restart();
                } else if (loadingInfo.status === WebEngineView.LoadSucceededStatus) {
                    full.unreachable = false;
                }
            }

            // The pages open nothing today; anything they might would go to the browser.
            onNewWindowRequested: request => Qt.openUrlExternally(request.requestedUrl)
        }

        // The daemon may start after the desktop, or restart on update.
        Timer {
            id: retry
            interval: 3000
            onTriggered: web.url = root.pageUrl
        }

        Kirigami.PlaceholderMessage {
            anchors.centerIn: parent
            width: parent.width - Kirigami.Units.largeSpacing * 2
            visible: full.unreachable
            icon.name: "network-disconnect"
            text: i18n("Widget service not running")
            explanation: i18n("Start it with:\nsystemctl --user start opnsense-widgetsd")
        }
    }
}
