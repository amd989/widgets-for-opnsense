import QtQuick
import QtQuick.Controls as QQC2
import QtQuick.Layouts
import org.kde.kirigami as Kirigami
import org.kde.kcmutils as KCM

KCM.SimpleKCM {
    property alias cfg_port: portField.value

    Kirigami.FormLayout {
        QQC2.SpinBox {
            id: portField
            Kirigami.FormData.label: i18n("Service port:")
            from: 1024
            to: 65535
            textFromValue: value => String(value) // no thousands separator
        }

        QQC2.Label {
            Layout.maximumWidth: Kirigami.Units.gridUnit * 20
            wrapMode: Text.WordWrap
            font: Kirigami.Theme.smallFont
            text: i18n("Port of the opnsense-widgetsd service on this computer (default 47820). The router URL and API key are set inside the widget: right-click it and choose OPNsense Connection.")
        }
    }
}
