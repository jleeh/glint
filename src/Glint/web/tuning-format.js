'use strict';
/* Shared by the Tuning page (app.js) and the tuning monitor window (monitor.js): turns the AI transcript and the test
   history into formatted markup. Everything that lands in markup is escaped first; numbers go through num(). No links,
   no images, no raw HTML: Markdown here is text structure only (headings, lists, tables, bold, italic, code). */
(() => {
  const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const num = (v, d = 0) => { const n = typeof v === 'number' ? v : typeof v === 'string' && v.trim() !== '' ? Number(v) : NaN; return Number.isFinite(n) ? n : d; };
  const pm = v => (num(v) >= 0 ? '+' : '−') + Math.abs(num(v));          // +250 / −15

  /* ---------------------------------------------------------------- Markdown (safe subset) */

  // inline: input is ALREADY escaped. Code spans are lifted out first so their contents stay literal.
  function inline(t) {
    const codes = [];
    t = t.replace(/`([^`]+)`/g, (_, c) => { codes.push(c); return '\u0001' + (codes.length - 1) + '\u0002'; });
    t = t.replace(/\*\*([^*]+?)\*\*/g, '<strong>$1</strong>')
         .replace(/(^|[^*\w])\*([^*\s][^*]*?)\*(?!\*)/g, '$1<em>$2</em>')
         .replace(/\[([^\]]+)\]\([^)]*\)/g, '$1');                       // links are not rendered: keep the words only
    return t.replace(/\u0001(\d+)\u0002/g, (_, i) => '<code>' + codes[+i] + '</code>');
  }
  const cells = line => line.trim().replace(/^\|/, '').replace(/\|$/, '').split('|').map(c => c.trim());
  const isSep = line => /^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$/.test(line) && line.includes('-');

  function md(src) {
    const lines = String(src ?? '').replace(/[\u0000-\u0002]/g, '').replace(/\r/g, '').split('\n');
    const out = []; let i = 0;
    while (i < lines.length) {
      const raw = lines[i], line = esc(raw);
      if (/^\s*$/.test(raw)) { i++; continue; }
      if (/^\s*```/.test(raw)) {                                          // fenced code
        const buf = []; i++;
        while (i < lines.length && !/^\s*```/.test(lines[i])) buf.push(esc(lines[i++]));
        i++; out.push('<pre class="md-pre">' + buf.join('\n') + '</pre>'); continue;
      }
      const h = /^\s{0,3}(#{1,4})\s+(.*)$/.exec(raw);
      if (h) { out.push(`<h${h[1].length + 2} class="md-h">${inline(esc(h[2]))}</h${h[1].length + 2}>`); i++; continue; }
      if (/^\s{0,3}([-*_])(\s*\1){2,}\s*$/.test(raw)) { out.push('<hr>'); i++; continue; }
      if (raw.includes('|') && i + 1 < lines.length && isSep(lines[i + 1])) {   // pipe table
        const head = cells(raw); i += 2; const rows = [];
        while (i < lines.length && lines[i].includes('|') && !/^\s*$/.test(lines[i])) rows.push(cells(lines[i++]));
        out.push('<div class="md-tablewrap"><table class="md-table"><thead><tr>' + head.map(c => `<th>${inline(esc(c))}</th>`).join('') + '</tr></thead><tbody>'
          + rows.map(r => '<tr>' + head.map((_, k) => `<td>${inline(esc(r[k] ?? ''))}</td>`).join('') + '</tr>').join('') + '</tbody></table></div>');
        continue;
      }
      const li = /^\s*([-*+]|\d+[.)])\s+/.exec(raw);
      if (li) {                                                            // list (one level; continuation lines join the item)
        const ordered = /\d/.test(li[1]); const items = [];
        while (i < lines.length) {
          const m = /^\s*([-*+]|\d+[.)])\s+(.*)$/.exec(lines[i]);
          if (m && /\d/.test(m[1]) === ordered) { items.push(m[2]); i++; }
          else if (items.length && /^\s{2,}\S/.test(lines[i])) { items[items.length - 1] += ' ' + lines[i].trim(); i++; }
          else break;
        }
        out.push(`<${ordered ? 'ol' : 'ul'} class="md-list">` + items.map(t => `<li>${inline(esc(t))}</li>`).join('') + `</${ordered ? 'ol' : 'ul'}>`);
        continue;
      }
      const para = [line]; i++;                                            // paragraph: until a blank line or a block start
      while (i < lines.length && !/^\s*$/.test(lines[i]) && !/^\s*(```|#{1,4}\s|([-*+]|\d+[.)])\s)/.test(lines[i]) && !(lines[i].includes('|') && i + 1 < lines.length && isSep(lines[i + 1]))) para.push(esc(lines[i++]));
      out.push('<p class="md-p">' + inline(para.join('<br>')) + '</p>');
    }
    return out.join('');
  }

  /* ---------------------------------------------------------------- tool calls → friendly steps */

  // transcript tool lines look like:  → tuning.gpu.test {"core":250,"memAb":0,"seconds":45}   (the JSON may be cut off at 160 chars)
  function parseTool(line) {
    const m = /^→\s*(\S+)\s*(.*)$/.exec(String(line));
    if (!m) return null;
    let args = {};
    try { const j = JSON.parse(m[2] || '{}'); if (j && typeof j === 'object') args = j; }
    catch { for (const k of ['core', 'memAb', 'seconds', 'light', 'force']) { const r = new RegExp('"' + k + '"\\s*:\\s*(-?[\\d.]+|true|false)').exec(m[2]); if (r) args[k] = r[1] === 'true' ? true : r[1] === 'false' ? false : Number(r[1]); } }
    return { tool: m[1], args };
  }
  const own = (o, k) => Object.prototype.hasOwnProperty.call(o, k) ? o[k] : undefined;
  const setting = a => `core ${pm(a.core)} / memory ${pm(a.memAb ?? 0)}`;
  const STEPS = {
    'tuning.gpu.test': a => `Test ${setting(a)}${a.seconds != null ? ' · ' + num(a.seconds) + ' s' : ''}${a.light ? ' (light)' : ''}`,
    'tuning.status': () => 'Read status',
    'tuning.history': () => 'Read the test history',
    'tuning.wait': () => null,                                             // the progress bar covers waiting
    'tuning.stop': () => 'Stop the running test',
    'tuning.gpu.apply': a => `Apply ${setting(a)}`,
    'tuning.gpu.save': a => `Save ${setting(a)}`,
    'tuning.gpu.stock': () => 'Back to stock',
    'tuning.crashes': () => 'Check crash history',
    'tuning.cpu.status': () => 'Read CPU status',
    'tuning.cpu.start': () => 'Start the CPU search',
    'tuning.cpu.finish': () => 'Finish the CPU search',
    'tuning.cpu.stop': () => 'Stop the CPU search',
  };
  /** Friendly text for a tool call line: a string, '' to hide it (tuning.wait), or null when the line isn't a tool call. */
  function stepText(line) {
    const t = parseTool(line); if (!t) return null;
    const f = own(STEPS, t.tool);
    if (!f) return 'Run ' + t.tool.replace(/^tuning\./, '');
    return f(t.args) ?? '';
  }

  /* ---------------------------------------------------------------- test results */

  const fmt = (v, d) => num(v).toFixed(d);
  /** Chips for a test result: throttled by temperature / power limit (a limit, not a failure). */
  function limitChips(r) {
    return (r.thermalLimited ? '<span class="tf-chip warn" title="The card reached its own temperature limit and throttled">thermal-limited</span>' : '')
         + (r.powerLimited ? `<span class="tf-chip warn" title="The card hit its power limit${r.powerLimitW ? ' (' + fmt(r.powerLimitW, 0) + ' W)' : ''}">power-limited</span>` : '');
  }
  /** One finished test as a card. */
  function resultCard(r) {
    const stats = [
      r.loadMhz ? ['Clock', num(r.loadMhz) + ' MHz'] : null,
      r.rate ? ['Compute', fmt(r.rate, 0) + ' G ops/s'] : null,
      r.gBps ? ['VRAM', fmt(r.gBps, 0) + ' GB/s'] : null,
      r.powerW ? ['Power', fmt(r.powerW, 0) + ' W'] : null,
      r.peakCoreC ? ['Peak', fmt(r.peakCoreC, 0) + ' °C'] : null,
      r.peakMemC ? ['VRAM temp', fmt(r.peakMemC, 0) + ' °C'] : null,
    ].filter(Boolean);
    return `<div class="tf-res ${r.ok ? 'pass' : 'fail'}">
      <div class="tf-res-head"><span class="tf-badge ${r.ok ? 'pass' : 'fail'}">${r.ok ? 'PASS' : 'FAIL'}</span><b>${esc(setting(r))}</b>
        <span class="muted">${num(r.seconds)} s${r.light ? ' · light' : ''}</span>${limitChips(r)}</div>
      ${stats.length ? '<div class="tf-stats">' + stats.map(([k, v]) => `<span><em>${k}</em>${esc(v)}</span>`).join('') + '</div>' : ''}
      ${r.why ? `<div class="tf-why">${esc(r.why)}</div>` : ''}</div>`;
  }

  /** Progress of the running test: { pct, elapsed, total } from testing.startedAt + seconds (null when nothing runs). */
  function progress(testing, nowMs) {
    if (!testing) return null;
    const total = Math.max(1, num(testing.seconds, 1));
    const t0 = Date.parse(testing.startedAt);
    const elapsed = Number.isFinite(t0) ? Math.min(total, Math.max(0, ((nowMs ?? Date.now()) - t0) / 1000)) : 0;
    return { pct: Math.round(elapsed / total * 1000) / 10, elapsed, total };
  }

  /* ---------------------------------------------------------------- the conversation */

  /** Render the AI transcript as formatted blocks. status = TuningService.Status() (for test results and the running test). */
  function timeline(ai, status) {
    ai = ai ?? {}; status = status ?? {};
    const lines = Array.isArray(ai.transcript) ? ai.transcript : [];
    const t0 = ai.startedAt ? Date.parse(ai.startedAt) - 3000 : 0;
    const hist = (Array.isArray(status.history) ? status.history : []).filter(r => !(t0 && Date.parse(r.time) < t0)).slice().reverse();   // oldest first
    const used = new Set();
    const out = [];
    const tests = lines.filter(l => parseTool(l)?.tool === 'tuning.gpu.test').length;
    let seen = 0;
    const sum = (ai.summary ?? '').trim();
    for (const raw of lines) {
      const line = String(raw ?? '');
      if (sum && !ai.running && line.trim() === sum) continue;           // the final message is shown once, as the summary
      const t = parseTool(line);
      if (t) {
        const txt = stepText(line);
        if (txt === '') continue;                                          // tuning.wait
        out.push(`<div class="tf-step"><span class="tf-ico">▸</span>${esc(txt)}</div>`);
        if (t.tool === 'tuning.gpu.test') {
          seen++;
          const idx = hist.findIndex((r, k) => !used.has(k) && num(r.core) === num(t.args.core) && num(r.memAb) === num(t.args.memAb ?? 0));
          if (idx >= 0) { used.add(idx); out.push(resultCard(hist[idx])); }
          else if (seen === tests && status.testing && num(status.testing.core) === num(t.args.core) && num(status.testing.memAb) === num(t.args.memAb ?? 0))
            out.push('<div class="tf-res run"><div class="tf-res-head"><span class="tf-badge run">RUNNING</span><b>' + esc(setting(status.testing)) + '</b></div></div>');
        }
      } else if (line.startsWith('!')) out.push(`<div class="tf-err">${esc(line.replace(/^!\s*/, ''))}</div>`);
      else if (/^(Starting Claude Code \(|Stopped by the user$|Finished$|Ended: )/.test(line)) out.push(`<div class="tf-note">${esc(line)}</div>`);
      else out.push(`<div class="tf-say">${md(line)}</div>`);
    }
    if (ai.summary && !ai.running) out.push(`<div class="tf-summary"><div class="tf-sum-title">Summary</div>${md(ai.summary)}</div>`);
    return out.join('');
  }

  window.TF = { esc, num, pm, md, parseTool, stepText, resultCard, limitChips, progress, timeline };
})();
