namespace OPNsenseWidgets.Configuration;

public interface ISettingsManager
{
    WidgetSettings Load();
    void Save(WidgetSettings settings);
}
