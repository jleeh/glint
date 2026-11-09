'use strict';
/* Tuning monitor window: a small always-readable view of what the tuner is doing. The host pushes {type:'tuning', status, ai, cpu}
   about once a second while this window is open; nothing runs here otherwise. */
const bridge = window.chrome?.webview ?? window.mockBridge;
const { esc, num } = TF;
const $ = s => document.querySelector(s);
const send = m => bridge?.postMessage(m);
const M = { msg: null, top: false };
const now = () => (typeof window.__now === 'function' ? window.__now() : Date.now());

/* pct null = nothing to fill (a count, an unknown total): no bar at all rather than one that never moves */
function gauge(label, valueHtml, pct, marks, tone, sub) {
  const p = Math.max(0, Math.min(100, num(pct)));
  const bar = pct == null ? '' : `<div class="mg-bar"><i class="${tone}" style="width:${p}%"></i>${(marks ?? []).map(m => `<b class="mk ${m.cls}" style="left:${Math.max(0, Math.min(100, num(m.at)))}%" title="${esc(m.title)}"></b>`).join('')}</div>`;
  return `<div class="mg"><div class="mg-label">${esc(label)}</div><div class="mg-val">${valueHtml}</div>
    ${bar}
    <div class="mg-sub">${sub ?? ''}</div></div>`;
}
const unit = (v, u, d = 0) => v == null ? '<span class="muted">–</span>' : `${num(v).toFixed(d)}<small>${u}</small>`;

/* CPU undervolt search: which core is under test, how far it is, temperature vs the stop limit, power, clock, per-core values. */
function renderCpu(cpu) {
  const lv = cpu.search?.live ?? {}, sr = cpu.search ?? {}, abort = num(lv.abortC, 90), scale = Math.max(abort + 10, 100);
  const t = lv.tempC, pct = lv.seconds ? num(lv.elapsed) / num(lv.seconds) * 100 : null;
  const secs = lv.seconds ? ` · ${num(lv.elapsed).toFixed(0)}/${num(lv.seconds).toFixed(0)} s` : '';
  const sweepAll = Array.isArray(lv.sweep) ? lv.sweep : [], kind = lv.sweepPass === 'one' ? 'one' : 'all';
  const sweep = sweepAll.filter(p => (p.kind ?? 'all') === kind), best = sweep.filter(p => p.ok && p.score != null && p.value !== 0).reduce((a, p) => (a == null || num(p.score) > num(a.score) ? p : a), null);
  const now = lv.phase === 'sweep'
    ? gauge('Step 1 of 3: sweep', lv.value == null ? unit(null) : `${num(lv.value)}<small> ${lv.sweepPass === 'one' ? 'one core' : 'all cores'}</small>`, pct, [], 'accent', `${lv.sweepPass === 'one' ? 'single-core check of the best values' : 'finding the best value'}${secs}`)
    : lv.phase === 'soak' || lv.phase === 'complete'
      ? gauge('Step 3 of 3: soak', `all cores<small>${lv.soakTries ? ` · try ${num(lv.soakTries) + 1}` : ''}</small>`, pct, [], 'accent', `full load at the final values${secs}`)
      : gauge(lv.phase === 'cores' ? 'Step 2 of 3: each core' : 'Core under test', lv.core == null ? unit(null) : `C${num(lv.core)}<small> at ${num(lv.value)}</small>`, pct, [], 'accent', lv.core == null ? 'starting' : `${lv.stage === 'confirming' ? `confirming ${num(lv.pass)}/${num(lv.passesNeeded)}` : esc(lv.stage ?? 'testing')}${lv.deepest != null ? ` · stable at ${num(lv.deepest)}` : ''}${secs}`);
  const prog = lv.phase === 'sweep'
    ? gauge('Sweep', `${num(sweep.length)}<small> readings</small>`, null, [], 'good', best ? `best so far ${num(best.value)} (${num(best.score) >= 0 ? '+' : ''}${num(best.score).toFixed(1)}%)` : 'measuring stock first')
    : gauge('Cores done', `${num(lv.coresDone)}<small> / ${num(lv.coresTotal)}</small>`, lv.coresTotal ? num(lv.coresDone) / num(lv.coresTotal) * 100 : 0, [], 'good', lv.picked != null ? `sweep picked ${num(lv.picked)}` : `${num(sr.adjustments)} adjustment${sr.adjustments === 1 ? '' : 's'}`);
  const items0 = [
    gauge('CPU temp', unit(t, ' °C'), t == null ? 0 : t / scale * 100, [{ at: abort / scale * 100, cls: 'bad', title: `Search stops at ${abort} °C` }],
      t == null ? '' : t >= abort - 3 ? 'bad' : t >= abort - 10 ? 'warn' : 'good', `stops at ${abort} °C`),
    gauge('Package power', lv.powerW == null ? unit(null) : `${num(lv.powerW).toFixed(0)}<small> W</small>`, lv.powerW == null ? 0 : Math.min(100, num(lv.powerW) / 250 * 100), [], 'accent', ''),
    gauge('Core clock', unit(lv.maxMhz == null ? null : Math.round(num(lv.maxMhz)), ' MHz'), lv.maxMhz == null ? 0 : Math.min(100, num(lv.maxMhz) / 6000 * 100), [], 'accent', 'fastest core'),
    gauge('Hardware errors', `${num(sr.wheaSinceStart)}`, null, [], num(sr.wheaSinceStart) ? 'bad' : 'good', 'WHEA since start'),
  ];
  const items = [items0[0], now, prog, ...items0.slice(1)];
  $('#m-gauges').innerHTML = items.join('');
  if (lv.phase === 'sweep') { $('#m-chips').innerHTML = sweep.map(p => `<span class="tf-chip ${p.ok ? (best && p.value === best.value ? 'ok' : '') : 'warn'}" title="${esc(p.ok ? `${p.score ?? '–'}% vs no undervolt (${p.kind === 'one' ? 'one core' : 'all cores'})${p.mhz != null ? `, ${num(p.mhz).toFixed(0)} MHz effective` : ''}${p.powerW != null ? `, ${num(p.powerW).toFixed(0)} W` : ''}` : p.why ?? 'failed')}">${num(p.value)}: ${!p.ok ? '✗' : p.score == null ? '–' : `${num(p.score) >= 0 ? '+' : ''}${num(p.score).toFixed(1)}%`}</span>`).join(''); return; }
  const vals = Array.isArray(lv.values) ? lv.values : (sr.values ?? []), passes = Array.isArray(lv.passes) ? lv.passes : (sr.passes ?? []), need = num(lv.passesNeeded, 2);
  $('#m-chips').innerHTML = vals.map((v, i) => {
    const done = Array.isArray(lv.done) ? !!lv.done[i] : (num(passes[i]) >= need || num(v) >= 0), cur = i === lv.core;
    return `<span class="tf-chip ${cur ? 'warn' : done ? 'ok' : ''}" title="core ${i}${done ? ': done' : ''}">C${i} ${num(v)}${done ? ' ✓' : ''}</span>`;
  }).join('');
}

/* CPU without a search running: the load test live (all cores, then one core), or just the CPU's readings while a CPU tune is between steps. */
function renderCpuLoad(cpu) {
  const lt = cpu.loadTest?.live, abort = num(lt?.abortC ?? cpu.curveOptimizer?.abortC, 90), scale = Math.max(abort + 10, 100);
  const t = lt ? lt.tempC : cpu.cpu?.tempC;
  const items = [
    gauge('CPU temp', unit(t, ' °C'), t == null ? 0 : num(t) / scale * 100, [{ at: abort / scale * 100, cls: 'bad', title: `Load test stops at ${abort} °C` }],
      t == null ? '' : num(t) >= abort - 3 ? 'bad' : num(t) >= abort - 10 ? 'warn' : 'good', `stops at ${abort} °C`),
    gauge(lt ? 'Load test' : 'CPU', lt ? (lt.step === 'one' ? 'one core' : 'all cores') : '<span class="muted">waiting</span>', lt?.seconds ? num(lt.elapsed) / num(lt.seconds) * 100 : null, [], 'accent',
      lt ? `${lt.step === 'one' ? 'single-core speed' : 'heat, power and clocks under full load'} · ${num(lt.elapsed).toFixed(0)}/${num(lt.seconds).toFixed(0)} s` : 'between steps'),
    gauge('Package power', lt?.powerW == null ? unit(null) : `${num(lt.powerW).toFixed(0)}<small> W</small>`, lt?.powerW == null ? 0 : Math.min(100, num(lt.powerW) / 250 * 100), [], 'accent', ''),
    gauge('Core clock', unit(lt?.avgMhz == null ? null : Math.round(num(lt.avgMhz)), ' MHz'), lt?.avgMhz == null ? 0 : Math.min(100, num(lt.avgMhz) / 6000 * 100), [], 'accent',
      lt?.maxMhz != null ? `average · fastest ${Math.round(num(lt.maxMhz))} MHz` : 'average'),
  ];
  $('#m-gauges').innerHTML = items.join('');
  $('#m-chips').innerHTML = '';
}

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

const tgt = ai => { const t = TF.aiTarget(ai); return t === 'all' ? 'GPU and CPU' : t.toUpperCase(); };
const brief = ai => { const b = TF.aiBrief(ai, 140); return b ? ' · ' + b : ''; };   // shown with textContent
function headline(m) {
  const st = m.status ?? {}, ai = m.ai ?? {}, cpu = m.cpu ?? {};
  if (m.error) return ['bad', 'Status unavailable', m.error];
  if (st.testing) return ['run', `Testing core ${TF.pm(st.testing.core)} / memory ${TF.pm(st.testing.memAb)} · ${num(st.testing.seconds)} s${st.testing.light ? ' (light)' : ''}`,
    ai.running ? `AI tuning ${tgt(ai)}${brief(ai)}` : 'Manual test'];
  const lt = cpu.loadTest?.live;
  if (lt) return ['run', `CPU load test: ${lt.step === 'one' ? 'one core' : 'all cores'} · ${num(lt.elapsed).toFixed(0)}/${num(lt.seconds).toFixed(0)} s`, ai.running ? `AI tuning ${tgt(ai)}${brief(ai)}` : 'Manual load test'];
  if (ai.running) return ['run', `AI tuning ${tgt(ai)}…`, (st.busy && st.progress ? st.progress : 'Claude is deciding the next step') + brief(ai)];
  if (st.busy) return ['run', st.progress || st.phase || 'Working…', ''];
  if (cpu.phase === 'searching') { const lv = cpu.search?.live ?? {}; return ['run', lv.core != null ? `Testing CPU core ${num(lv.core)} at ${num(lv.value)}` : 'CPU undervolt search', `${num(lv.coresDone)}/${num(lv.coresTotal)} cores done · per-core undervolt search`]; }
  return ['idle', 'Idle', ai.error ? 'Last run: ' + ai.error : ai.summary ? 'Last run finished' + brief(ai) : 'No test running'];
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
  const cpuRun = m.cpu?.phase === 'searching' && !st.busy;
  $('#m-stop').disabled = !(ai.running || st.busy || m.cpu?.phase === 'searching' || m.cpu?.loadTest?.running);
  renderProgress(st);
  if (cpuRun) {
    const lv = m.cpu.search?.live ?? {};
    $('#m-prog').hidden = !lv.seconds; $('#m-prog-fill').style.width = (lv.seconds ? Math.min(100, num(lv.elapsed) / num(lv.seconds) * 100) : 0) + '%';
    const what = lv.phase === 'sweep' ? `sweep ${num(lv.value)}` : lv.phase === 'soak' ? 'soak' : `core ${num(lv.core)}`;
    $('#m-prog-text').textContent = lv.seconds ? `${what}: ${Math.floor(num(lv.elapsed))} s of ${Math.round(num(lv.seconds))} s` : '';
    renderCpu(m.cpu);
  } else if (m.cpu?.loadTest?.live) {
    const lt = m.cpu.loadTest.live;
    $('#m-prog').hidden = !lt.seconds; $('#m-prog-fill').style.width = (lt.seconds ? Math.min(100, num(lt.elapsed) / num(lt.seconds) * 100) : 0) + '%';
    $('#m-prog-text').textContent = lt.seconds ? `${Math.floor(num(lt.elapsed))} s of ${Math.round(num(lt.seconds))} s` : '';
    renderCpuLoad(m.cpu);
  } else if (ai.running && TF.aiTarget(ai) === 'cpu' && !st.testing) renderCpuLoad(m.cpu);
  else renderGauges(st);
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
