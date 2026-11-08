'use strict';
/* Tuning monitor window: a small always-readable view of what the tuner is doing. The host pushes {type:'tuning', status, ai, cpu}
   about once a second while this window is open; nothing runs here otherwise. */
const bridge = window.chrome?.webview ?? window.mockBridge;
const { esc, num } = TF;
const $ = s => document.querySelector(s);
const send = m => bridge?.postMessage(m);
const M = { msg: null, top: false };
const now = () => (typeof window.__now === 'function' ? window.__now() : Date.now());

function gauge(label, valueHtml, pct, marks, tone, sub) {
  const p = Math.max(0, Math.min(100, num(pct)));
  return `<div class="mg"><div class="mg-label">${esc(label)}</div><div class="mg-val">${valueHtml}</div>
    <div class="mg-bar"><i class="${tone}" style="width:${p}%"></i>${(marks ?? []).map(m => `<b class="mk ${m.cls}" style="left:${Math.max(0, Math.min(100, num(m.at)))}%" title="${esc(m.title)}"></b>`).join('')}</div>
    <div class="mg-sub">${sub ?? ''}</div></div>`;
}
const unit = (v, u, d = 0) => v == null ? '<span class="muted">–</span>' : `${num(v).toFixed(d)}<small>${u}</small>`;

function renderGauges(st) {
  const live = st.live, g = st.gpu ?? {}, L = st.limits ?? {};
  const slow = num(g.slowdownC, 0), emC = num(L.emergencyCoreC, 90), emM = num(L.emergencyMemC, 100);
  const tone = (v, warnAt, badAt) => v >= badAt ? 'bad' : v >= warnAt ? 'warn' : 'good';
  const scaleC = Math.max(emC + 8, 100), scaleM = Math.max(emM + 8, 110);
  const pl = g.powerLimitW ?? {}, plMax = num(pl.max, 0) || num(pl.default, 0) || 1, plNow = num(pl.now, 0);
  const core = live?.coreC, mem = live?.memC, pw = live?.powerW;
  const items = [
    gauge('GPU core', unit(core, ' °C'), core == null ? 0 : core / scaleC * 100,
      [slow ? { at: slow / scaleC * 100, cls: 'warn', title: `Card slows down at ${slow} °C` } : null, { at: emC / scaleC * 100, cls: 'bad', title: `Emergency stop at ${emC} °C` }].filter(Boolean),
      core == null ? '' : tone(core, (slow || emC - 8) - 5, emC - 3),
      `slows ${slow || '–'} · stop ${emC}`),
    mem != null || live == null ? gauge('Memory temp', unit(mem, ' °C'), mem == null ? 0 : mem / scaleM * 100,
      [{ at: emM / scaleM * 100, cls: 'bad', title: `Emergency stop at ${emM} °C` }], mem == null ? '' : tone(mem, emM - 14, emM - 4), mem == null ? 'not reported' : `stop ${emM} °C`) : '',
    gauge('Core clock', unit(live?.mhz, ' MHz'), live?.mhz == null ? 0 : Math.min(100, num(live.mhz) / 3600 * 100), [], 'accent', live?.memMhz != null ? `memory ${num(live.memMhz)} MHz` : ''),
    gauge('Power', pw == null ? unit(null) : `${num(pw).toFixed(0)}<small> W</small>`, pw == null ? 0 : num(pw) / plMax * 100,
      plNow && plMax ? [{ at: plNow / plMax * 100, cls: 'warn', title: `Power limit now ${plNow} W` }] : [], pw != null && plNow && pw >= plNow * 0.97 ? 'warn' : 'accent',
      plMax > 1 ? `limit ${plNow || '–'} W (card max ${num(pl.max)})` : ''),
    gauge('Fan', unit(live?.fanPct, ' %'), live?.fanPct ?? 0, [], 'accent', ''),
    gauge('GPU load', unit(live?.utilPct, ' %'), live?.utilPct ?? 0, [], 'accent', ''),
  ];
  $('#m-gauges').innerHTML = items.join('');
  const th = Array.isArray(live?.throttle) ? live.throttle : [];
  const chips = [];
  if (th.some(x => /thermal/i.test(String(x)))) chips.push('<span class="tf-chip warn" title="The card is slowing itself down because of temperature">thermal-limited</span>');
  if (th.some(x => /power/i.test(String(x)))) chips.push('<span class="tf-chip warn" title="The card is holding back at its power limit">power-limited</span>');
  th.filter(x => !/thermal|power/i.test(String(x))).slice(0, 3).forEach(x => chips.push(`<span class="tf-chip">${esc(x)}</span>`));
  $('#m-chips').innerHTML = chips.join('');
}

function headline(m) {
  const st = m.status ?? {}, ai = m.ai ?? {}, cpu = m.cpu ?? {};
  if (m.error) return ['bad', 'Status unavailable', m.error];
  if (st.testing) return ['run', `Testing core ${TF.pm(st.testing.core)} / memory ${TF.pm(st.testing.memAb)} · ${num(st.testing.seconds)} s${st.testing.light ? ' (light)' : ''}`,
    ai.running ? `AI tuning ${ai.goal === 'all' ? 'GPU and CPU' : String(ai.goal ?? 'gpu').toUpperCase()}` : 'Manual test'];
  if (ai.running) return ['run', `AI tuning ${ai.goal === 'all' ? 'GPU and CPU' : String(ai.goal ?? 'gpu').toUpperCase()}…`, st.busy && st.progress ? st.progress : 'Claude is deciding the next step'];
  if (st.busy) return ['run', st.progress || st.phase || 'Working…', ''];
  if (cpu.search?.running) return ['run', 'CPU search running', 'per-core Curve Optimizer search (hours)'];
  return ['idle', 'Idle', ai.error ? 'Last run: ' + ai.error : ai.summary ? 'Last run finished' : 'No test running'];
}

function renderProgress(st) {
  const p = TF.progress(st.testing, now());
  $('#m-prog').hidden = !p;
  $('#m-prog-fill').style.width = (p ? p.pct : 0) + '%';
  $('#m-prog-text').textContent = p ? `${Math.floor(p.elapsed)} s of ${Math.round(p.total)} s` : '';
}

function apply(m) {
  M.msg = m;
  const st = m.status ?? {}, ai = m.ai ?? {};
  const [kind, title, sub] = headline(m);
  $('#m-dot').className = 'mon-state-dot ' + kind;
  $('#m-title').textContent = title; $('#m-sub').textContent = sub;
  $('#m-stop').disabled = !(ai.running || st.busy);
  renderProgress(st);
  renderGauges(st);
  const log = $('#m-log'), atEnd = log.scrollTop + log.clientHeight >= log.scrollHeight - 24;
  const html = TF.timeline(ai, st);
  const empty = '<div class="muted" style="padding:6px 2px">The AI\'s plan, each test and its result appear here while Tune with AI runs.</div>';
  const next = html || empty;
  if (log.__html !== next) { log.innerHTML = next; log.__html = next; if (atEnd) log.scrollTop = log.scrollHeight; }
  $('#m-clock').textContent = m.at ? new Date(m.at).toLocaleTimeString() : '';
}
window.monitorApply = apply;                       // the host pushes through the bridge; tests call this directly

bridge?.addEventListener('message', e => { const m = e.data; if (m?.type === 'tuning') apply(m); });
$('#m-stop').onclick = () => send({ type: 'action', op: 'stop' });
$('#m-top').onclick = () => { M.top = !M.top; $('#m-top').classList.toggle('on', M.top); send({ type: 'top', on: M.top }); };
send({ type: 'ready', page: 'monitor' });
