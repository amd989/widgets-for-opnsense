using System.Text.RegularExpressions;

namespace OPNsenseWidgets.Widgets.Firewall;

/// <summary>
/// Turns raw pf rule text (what OPNsense reports for rules without a description) into a short,
/// readable label, e.g.
///   "nat on vmx0 inet from (vmx1:network:1) to any -> (vmx0:0) port 1024:65535" -> "NAT · LAN net → WAN"
///   "block drop in log on ! vmx2 inet from 192.168.20.0/24 to any"              -> "Block in · not DMZ · from 192.168.20.0/24"
///   "scrub in all fragment reassemble"                                          -> "Scrub in · reassemble fragments"
/// Labels that are not pf syntax (rule descriptions) are returned unchanged.
/// </summary>
public static partial class PfRuleLabel
{
    private const string Sep = " · ";
    private const string Arrow = " → ";

    // Options that end the part of a rule worth showing.
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "keep", "modulate", "synproxy", "flags", "label", "allow-opts", "ridentifier", "tag", "tagged",
        "queue", "probability", "max-src-conn", "max-src-nodes", "max-src-states", "set", "route-to",
        "reply-to", "dup-to", "rtable", "static-port", "source-hash", "round-robin", "random", "bitmask",
        "sticky-address", "icmp-type", "icmp6-type", "user", "group", "tos", "os", "prio", "once"
    };

    private static readonly HashSet<string> Actions = new(StringComparer.OrdinalIgnoreCase)
    {
        "pass", "block", "match", "nat", "rdr", "binat", "no", "scrub", "anchor", "nat-anchor", "rdr-anchor"
    };

    public static string Pretty(string raw, IReadOnlyDictionary<string, string> interfaceNames)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return raw;

        var text = raw.Trim();
        var first = text.Split(' ', 2)[0];
        if (!Actions.Contains(first))
            return raw; // a rule description, already human

        try
        {
            return Parse(text, interfaceNames) ?? raw;
        }
        catch
        {
            return raw;
        }
    }

    private static string? Parse(string text, IReadOnlyDictionary<string, string> names)
    {
        // Split off the translation target of nat/rdr/binat ("... -> target").
        string? target = null;
        var arrow = text.IndexOf("->", StringComparison.Ordinal);
        if (arrow >= 0)
        {
            target = text[(arrow + 2)..].Trim();
            text = text[..arrow].Trim();
        }

        var t = Tokenize(text);
        var i = 0;
        string Next() => i < t.Count ? t[i++] : "";
        string Peek() => i < t.Count ? t[i] : "";

        var action = Next().ToLowerInvariant();
        var negatedAction = false;
        if (action == "no")
        {
            negatedAction = true;
            action = Next().ToLowerInvariant();
        }
        if (action is "anchor" or "nat-anchor" or "rdr-anchor")
            return "Anchor" + Sep + Trim(Next().Trim('"'));

        if (action == "block" && Peek() is "drop" or "return" or "return-rst" or "return-icmp")
            Next();

        string? direction = null, iface = null, proto = null, src = null, srcPort = null, dst = null, dstPort = null;
        var ipv6 = false;
        var all = false;
        var extras = new List<string>();

        while (i < t.Count)
        {
            var tok = Next();
            var low = tok.ToLowerInvariant();
            if (StopWords.Contains(low))
                break;

            switch (low)
            {
                case "in": case "out": direction = low; break;
                case "log": case "quick": break;
                case "inet": break;
                case "inet6": ipv6 = true; break;
                case "on":
                    var negate = Peek() == "!";
                    if (negate) Next();
                    iface = (negate ? "not " : "") + Iface(Next(), names);
                    break;
                case "proto":
                    proto = Next().Trim('{', '}').ToUpperInvariant();
                    break;
                case "all": all = true; break;
                case "from":
                    src = Address(ReadAddress(t, ref i), names);
                    if (Peek().Equals("port", StringComparison.OrdinalIgnoreCase)) { Next(); srcPort = ReadPort(t, ref i); }
                    break;
                case "to":
                    dst = Address(ReadAddress(t, ref i), names);
                    if (Peek().Equals("port", StringComparison.OrdinalIgnoreCase)) { Next(); dstPort = ReadPort(t, ref i); }
                    break;
                case "fragment":
                    if (Peek().Equals("reassemble", StringComparison.OrdinalIgnoreCase)) { Next(); extras.Add("reassemble fragments"); }
                    break;
                case "no-df": extras.Add("clear DF"); break;
                case "random-id": extras.Add("random IP id"); break;
                case "max-mss": extras.Add("MSS " + Next()); break;
                case "min-ttl": extras.Add("min TTL " + Next()); break;
            }
        }

        var family = ipv6 ? "IPv6" : null;
        var from = Endpoint(src, srcPort);
        var to = Endpoint(dst, dstPort);

        switch (action)
        {
            case "nat":
                if (negatedAction)
                    return Join("No NAT", iface, proto ?? (all ? null : Flow(from, to)));
                var outIface = iface ?? (target is null ? null : Target(target, names));
                // Outbound NAT sources are nearly always interface networks: "LAN → WAN" is enough.
                var natFrom = from?.Replace(" net", "");
                return Join("NAT", proto, natFrom is null or "any" ? outIface : natFrom + Arrow + outIface, family);

            case "rdr":
                if (negatedAction)
                    return Join("No redirect", iface, proto ?? (all ? null : Flow(from, to)));
                // "WAN · :443" reads better than "WAN · WAN address:443"
                var rdrTo = iface is not null && dst == iface + " address" ? Endpoint(null, dstPort) : to;
                return Join("Redirect", iface, proto, (rdrTo ?? "any") + (target is null ? "" : Arrow + Target(target, names)), family);

            case "binat":
                return Join("1:1 NAT", iface, from + (target is null ? "" : Arrow + Target(target, names)), family);

            case "scrub":
                return Join(direction is null ? "Scrub" : "Scrub " + direction, iface, proto,
                    all ? null : Flow(from, to), extras.Count > 0 ? string.Join(", ", extras) : null, family);

            default: // pass / block / match
                var verb = action switch { "pass" => "Allow", "block" => "Block", _ => "Match" };
                if (direction is not null) verb += " " + direction;
                var flow = all ? null : Flow(from, to);
                if (flow is null && iface is null && proto is null)
                    flow = all ? "everything" : null;
                return Join(verb, iface, proto, flow, family);
        }
    }

    /// <summary>"from X" / "to Y" / "X → Y", or null when both sides are "any".</summary>
    private static string? Flow(string? from, string? to)
    {
        var anyFrom = from is null or "any";
        var anyTo = to is null or "any";
        if (anyFrom && anyTo) return null;
        if (anyTo) return "from " + from;
        if (anyFrom) return "to " + to;
        return from + Arrow + to;
    }

    private static string? Endpoint(string? address, string? port)
    {
        if (address is null && port is null) return null;
        if (port is null) return address;
        port = string.Join(",", port.Trim('{', '}').Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries));
        return (address is null or "any" ? "" : address) + ":" + port;
    }

    private static string Join(params string?[] parts) =>
        string.Join(Sep, parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    private static string ReadAddress(List<string> t, ref int i)
    {
        if (i >= t.Count) return "any";
        var tok = t[i++];
        if (tok == "!" && i < t.Count) tok = "!" + t[i++];
        return tok;
    }

    private static string ReadPort(List<string> t, ref int i)
    {
        if (i >= t.Count) return "";
        var tok = t[i++];
        // "port = 443", "port != 80", "port 1024:65535", "port 1024 >< 2048"
        if (tok is "=" or "!=" or "<" or ">" or "<=" or ">=" && i < t.Count)
            return (tok is "=" ? "" : tok) + t[i++];
        if (i + 1 < t.Count && t[i] is "><" or "<>" or ":")
        {
            var range = tok + "-" + t[i + 1];
            i += 2;
            return range;
        }
        return tok;
    }

    /// <summary>Interface device to its OPNsense name: vmx0 -> WAN.</summary>
    private static string Iface(string device, IReadOnlyDictionary<string, string> names)
    {
        device = device.Trim('(', ')');
        if (names.TryGetValue(device, out var name) && !string.IsNullOrWhiteSpace(name))
            return name;
        return device switch
        {
            "lo0" => "loopback",
            "enc0" => "IPsec",
            "pfsync0" => "pfsync",
            _ => device
        };
    }

    /// <summary>pf address syntax to words: (vmx1:network:1) -> "LAN net", (vmx0:0) -> "WAN address".</summary>
    private static string Address(string token, IReadOnlyDictionary<string, string> names)
    {
        var negate = token.StartsWith('!');
        var a = token.TrimStart('!');
        string result;

        var dyn = DynamicAddress().Match(a);
        if (dyn.Success)
        {
            var dev = dyn.Groups["dev"].Value;
            var mod = dyn.Groups["mod"].Value;
            if (dev.Equals("self", StringComparison.OrdinalIgnoreCase))
                result = "this firewall";
            else
                result = Iface(dev, names) + (mod.Equals("network", StringComparison.OrdinalIgnoreCase) ? " net" : " address");
        }
        else if (a.StartsWith('<') && a.EndsWith('>'))
            result = a[1..^1]; // table / alias name
        else if (a.Equals("self", StringComparison.OrdinalIgnoreCase))
            result = "this firewall";
        else
            result = a.Trim('{', '}');

        return negate ? "not " + result : result;
    }

    private static string Target(string target, IReadOnlyDictionary<string, string> names)
    {
        var tok = Tokenize(target);
        if (tok.Count == 0) return target;
        var address = Address(tok[0], names);
        var portIndex = tok.FindIndex(x => x.Equals("port", StringComparison.OrdinalIgnoreCase));
        if (portIndex >= 0 && portIndex + 1 < tok.Count)
        {
            var i = portIndex + 1;
            return address + ":" + ReadPort(tok, ref i);
        }
        return address;
    }

    /// <summary>Splits on spaces but keeps "{ a, b }" lists and quoted strings together.</summary>
    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var depth = 0;
        var quoted = false;
        foreach (var ch in text)
        {
            if (ch == '"') quoted = !quoted;
            if (!quoted && ch == '{') depth++;
            if (!quoted && ch == '}') depth = Math.Max(0, depth - 1);
            if (ch == ' ' && depth == 0 && !quoted)
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(ch);
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    private static string Trim(string s) => s.Length > 40 ? s[..40] : s;

    // (vmx1:network:1), vmx1:network, (vmx0:0), (vmx0), (self)
    [GeneratedRegex(@"^\(?(?<dev>[A-Za-z_][\w.]*)(?::(?<mod>network|broadcast|peer|0|\d+))?(?::\d+)?\)$|^(?<dev>[A-Za-z_][\w.]*):(?<mod>network|broadcast|peer)$")]
    private static partial Regex DynamicAddress();
}
