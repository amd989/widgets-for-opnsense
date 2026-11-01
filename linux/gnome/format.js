// Number and time formatting, matching fmt in Shared/Web/widget.js so the panel reads like the widgets.

function trimNumber(x) {
    if (x >= 100)
        return x.toFixed(0);
    if (x >= 10)
        return x.toFixed(1);
    return x.toFixed(2);
}

/** 182000000 -> "182 Mbps" */
export function bits(bps) {
    bps = Math.max(0, bps || 0);
    for (const [unit, div] of [['Gbps', 1e9], ['Mbps', 1e6], ['Kbps', 1e3]]) {
        if (bps >= div)
            return `${trimNumber(bps / div)} ${unit}`;
    }
    return `${Math.round(bps)} bps`;
}

/** 182000000 -> "182M", for the narrow panel label */
export function bitsShort(bps) {
    bps = Math.max(0, bps || 0);
    for (const [unit, div] of [['G', 1e9], ['M', 1e6], ['K', 1e3]]) {
        if (bps >= div)
            return `${trimNumber(bps / div)}${unit}`;
    }
    return `${Math.round(bps)}`;
}

/** 966269802 -> "966M" */
export function compact(n) {
    n = n || 0;
    for (const [suffix, div] of [['T', 1e12], ['B', 1e9], ['M', 1e6], ['K', 1e3]]) {
        if (Math.abs(n) >= div)
            return `${trimNumber(n / div)}${suffix}`;
    }
    return `${Math.round(n)}`;
}

/** <60s "now", <60m "Xm", <24h "Xh", else "Xd" */
export function relative(start, now) {
    if (start === null || start === undefined)
        return '';
    const s = Math.max(0, (now - start) / 1000);
    if (s < 60)
        return 'now';
    if (s < 3600)
        return `${Math.floor(s / 60)}m`;
    if (s < 86400)
        return `${Math.floor(s / 3600)}h`;
    return `${Math.floor(s / 86400)}d`;
}

export function time(ms) {
    return ms ? new Date(ms).toLocaleTimeString([], {hour: 'numeric', minute: '2-digit'}) : '';
}
