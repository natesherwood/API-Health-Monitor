/* RateMyAPI status page: React 18 UMD + Babel standalone, no build step. Data: GET /api/status. */
const { useState, useEffect, useCallback, useMemo } = React;

const REFRESH_MS = 15000;
const SEGMENTS = 90;      // hourly buckets in the uptime bar
const SPARK_HOURS = 24;   // hourly p95 points in the sparkline
const HOUR = 3600 * 1000;
const SEVERITY = { down: 0, degraded: 1, resolved: 2, ok: 3 };

function useNow(intervalMs) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const t = setInterval(() => setNow(Date.now()), intervalMs);
    return () => clearInterval(t);
  }, [intervalMs]);
  return now;
}

function useStatus() {
  const [state, setState] = useState({ data: null, error: null, fetchedAt: null });

  const load = useCallback(async () => {
    try {
      const res = await fetch("/api/status", { headers: { Accept: "application/json" }, cache: "no-store" });
      if (!res.ok) throw new Error("HTTP " + res.status);
      const data = await res.json();
      setState({ data, error: null, fetchedAt: Date.now() });
    } catch (e) {
      // Keep showing the last good payload; surface the failure.
      setState((s) => ({ ...s, error: e.message || String(e) }));
    }
  }, []);

  useEffect(() => {
    load();
    const t = setInterval(load, REFRESH_MS);
    return () => clearInterval(t);
  }, [load]);

  return state;
}

function ago(iso, now) {
  if (!iso) return "never";
  const s = Math.max(0, Math.round((now - Date.parse(iso)) / 1000));
  if (s < 60) return s + "s ago";
  const m = Math.floor(s / 60);
  if (m < 60) return m + "m ago";
  const h = Math.floor(m / 60);
  if (h < 48) return h + "h" + String(m % 60).padStart(2, "0") + "m ago";
  return Math.floor(h / 24) + "d ago";
}

function fmtMs(v) {
  return v == null ? "—" : v >= 10000 ? (v / 1000).toFixed(1) + "s" : v + "ms";
}

function fmtHour(t) {
  return new Date(t).toISOString().slice(5, 13).replace("T", " ") + ":00Z";
}

/** Fixed hourly slots ending at the current UTC hour; missing buckets are null (no data). */
function slots(buckets, generatedAt, count) {
  const end = Math.floor(Date.parse(generatedAt) / HOUR) * HOUR;
  const byHour = new Map(buckets.map((b) => [Date.parse(b.t), b]));
  const out = [];
  for (let i = count - 1; i >= 0; i--) {
    const t = end - i * HOUR;
    out.push({ t, b: byHour.get(t) || null });
  }
  return out;
}

function segmentColor(b, threshold) {
  if (!b || b.n === 0) return "var(--seg-empty)";
  if (b.up < 95) return "var(--red)";
  if (b.up < 99.5 || (b.p95 != null && b.p95 > threshold)) return "var(--amber)";
  return "var(--green)";
}

function UptimeBar({ monitor, generatedAt }) {
  const segs = useMemo(() => slots(monitor.buckets, generatedAt, SEGMENTS), [monitor.buckets, generatedAt]);
  const w = 4, gap = 1;
  return (
    <svg className="bar" viewBox={`0 0 ${SEGMENTS * w} 16`} preserveAspectRatio="none" role="img"
         aria-label={`${monitor.name}: hourly uptime, last ${SEGMENTS} hours`}>
      {segs.map(({ t, b }, i) => (
        <rect key={t} x={i * w} y="0" width={w - gap} height="16" fill={segmentColor(b, monitor.degraded_p95_ms)}>
          <title>
            {fmtHour(t)}{"  "}
            {b ? `uptime ${b.up.toFixed(2)}%  p95 ${fmtMs(b.p95)}  checks ${b.n}  failed ${b.f}` : "no data"}
          </title>
        </rect>
      ))}
    </svg>
  );
}

function Sparkline({ monitor, generatedAt }) {
  const pts = useMemo(() => slots(monitor.buckets, generatedAt, SPARK_HOURS), [monitor.buckets, generatedAt]);
  const W = 120, H = 22, PAD = 2;
  const values = pts.map((p) => (p.b && p.b.p95 != null ? p.b.p95 : null));
  const threshold = monitor.degraded_p95_ms;
  const max = Math.max(threshold * 1.25, ...values.filter((v) => v != null), 1);
  const x = (i) => PAD + (i * (W - 2 * PAD)) / (SPARK_HOURS - 1);
  const y = (v) => H - PAD - (v / max) * (H - 2 * PAD);

  // Break the line where there is no data instead of interpolating across gaps.
  const runs = [];
  let run = [];
  values.forEach((v, i) => {
    if (v == null) { if (run.length) runs.push(run); run = []; }
    else run.push(`${x(i).toFixed(1)},${y(v).toFixed(1)}`);
  });
  if (run.length) runs.push(run);

  const last = [...values].reverse().find((v) => v != null);
  const stroke = last != null && last > threshold ? "var(--amber)" : "var(--blue)";

  return (
    <svg className="spark" viewBox={`0 0 ${W} ${H}`} preserveAspectRatio="none" role="img"
         aria-label={`${monitor.name}: p95 latency, last ${SPARK_HOURS} hours`}>
      <line x1={PAD} x2={W - PAD} y1={y(threshold)} y2={y(threshold)} stroke="var(--faint)" strokeDasharray="2 2" strokeWidth="1" />
      {runs.map((r, i) =>
        r.length === 1
          ? <circle key={i} cx={r[0].split(",")[0]} cy={r[0].split(",")[1]} r="1.2" fill={stroke} />
          : <polyline key={i} points={r.join(" ")} fill="none" stroke={stroke} strokeWidth="1.2" strokeLinejoin="round" />
      )}
      <title>{`p95 per hour (dashed: ${threshold}ms degraded threshold)`}</title>
    </svg>
  );
}

function StatePill({ state, enabled }) {
  const s = enabled ? state : "disabled";
  return <span className={`pill s-${s}`}>{s}</span>;
}

function IncidentBanner({ incidents, now }) {
  if (!incidents.length) return null;
  const onlyDegraded = incidents.every((i) => i.state === "degraded");
  return (
    <div className={"banner" + (onlyDegraded ? " only-degraded" : "")} role="alert">
      {incidents.map((i) => (
        <div className="line" key={i.id}>
          <span className={`s-${i.state}`}>● {i.state.toUpperCase()}</span>
          <span className="name">{i.monitor_name}</span>
          <span className="reason">{i.trigger_reason}</span>
          <span className="when">opened {ago(i.opened_at, now)}{i.updated_at !== i.opened_at ? ` · changed ${ago(i.updated_at, now)}` : ""} · #{i.id}</span>
        </div>
      ))}
    </div>
  );
}

function MonitorRow({ m, generatedAt, now }) {
  const stale = m.enabled && m.last_checked_at && now - Date.parse(m.last_checked_at) > m.interval_seconds * 3000 + 5000;
  return (
    <tr>
      <td><StatePill state={m.state} enabled={m.enabled} /></td>
      <td title={m.name}>
        <div className="name">{m.name}</div>
        <div className="sub">every {m.interval_seconds}s · p95 &gt; {m.degraded_p95_ms}ms</div>
      </td>
      <td><UptimeBar monitor={m} generatedAt={generatedAt} /></td>
      <td className="num c-up">{m.uptime_pct == null ? "—" : m.uptime_pct.toFixed(2) + "%"}</td>
      <td className="c-spark"><Sparkline monitor={m} generatedAt={generatedAt} /></td>
      <td className="num c-p95" title="live p95 (sliding window)">{fmtMs(m.live_p95_ms)}</td>
      <td className={"num" + (stale ? " late" : "")} title={m.last_checked_at || ""}>
        <span className={m.last_success === false ? "s-down" : ""}>{ago(m.last_checked_at, now)}</span>
      </td>
    </tr>
  );
}

function App() {
  const { data, error, fetchedAt } = useStatus();
  const now = useNow(1000);

  if (!data) {
    return (
      <>
        <header><h1>RATEMYAPI <span className="slash">//</span> status</h1></header>
        {error ? <div className="error">status unavailable: {error} (retrying every {REFRESH_MS / 1000}s)</div> : <p className="meta">loading…</p>}
      </>
    );
  }

  const monitors = [...data.monitors].sort(
    (a, b) => (a.enabled === b.enabled ? 0 : a.enabled ? -1 : 1) || SEVERITY[a.state] - SEVERITY[b.state] || a.id - b.id
  );
  const counts = data.monitors.reduce((acc, m) => ((acc[m.enabled ? m.state : "disabled"] = (acc[m.enabled ? m.state : "disabled"] || 0) + 1), acc), {});
  const nextIn = fetchedAt ? Math.max(0, Math.ceil((fetchedAt + REFRESH_MS - now) / 1000)) : 0;
  const headline = { ok: "all systems operational", degraded: "degraded performance", down: "partial outage" }[data.overall];

  return (
    <>
      <header>
        <h1>RATEMYAPI <span className="slash">//</span> status</h1>
        <StatePill state={data.overall} enabled={true} />
        <span>{headline}</span>
        <span className="spacer" />
        <span className="meta">
          {data.monitors.length} monitors
          {["down", "degraded", "resolved", "disabled"].filter((k) => counts[k]).map((k) => ` · ${counts[k]} ${k}`).join("")}
        </span>
        <span className="meta">updated {ago(data.generated_at, now)} · refresh in {nextIn}s</span>
      </header>

      {error && <div className="error">refresh failed: {error}; showing data from {ago(data.generated_at, now)}</div>}
      <IncidentBanner incidents={data.incidents} now={now} />

      <table>
        <thead>
          <tr>
            <th className="c-state">state</th>
            <th className="c-name">monitor</th>
            <th className="c-bar">uptime · last {data.bucket_hours}h (hourly)</th>
            <th className="num c-up">uptime</th>
            <th className="c-spark">p95 · {SPARK_HOURS}h</th>
            <th className="num c-p95">p95 now</th>
            <th className="num c-last">checked</th>
          </tr>
        </thead>
        <tbody>
          {monitors.map((m) => <MonitorRow key={m.id} m={m} generatedAt={data.generated_at} now={now} />)}
          {monitors.length === 0 && <tr><td colSpan="7" className="meta">no monitors configured</td></tr>}
        </tbody>
      </table>

      <footer>
        <span className="legend"><i style={{ background: "var(--green)" }} />≥99.5% &amp; p95 ok</span>
        <span className="legend"><i style={{ background: "var(--amber)" }} />&lt;99.5% or p95 over threshold</span>
        <span className="legend"><i style={{ background: "var(--red)" }} />&lt;95%</span>
        <span className="legend"><i style={{ background: "var(--seg-empty)" }} />no data</span>
        <span className="spacer" />
        <span>bars &amp; sparklines: hourly rollups (materialized view) · state &amp; p95 now: live</span>
      </footer>
    </>
  );
}

ReactDOM.createRoot(document.getElementById("root")).render(<App />);
