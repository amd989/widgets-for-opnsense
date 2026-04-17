namespace OPNsenseWidgets.Api.Models;

public sealed class FirewallRuleStat
{
    public string Label { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public long Evaluations { get; set; }
    public long Packets { get; set; }
    public long Bytes { get; set; }
    public long States { get; set; }
    public long StateCreations { get; set; }

    public string EffectiveLabel =>
        !string.IsNullOrWhiteSpace(Description) ? Description :
        !string.IsNullOrWhiteSpace(Label) ? Label :
        "(unnamed rule)";
}
