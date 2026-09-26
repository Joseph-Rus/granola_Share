// Study Stash · Canvas: a read-only fetcher for your Study Stash library.
// The library decides what to read (assignments, submissions, modules, pages, files, or whatever a course's
// scout asks for); this extension only fetches those Canvas URLs with the session you're already
// signed into and hands the answers back. It never writes to Canvas, and it refuses any URL outside Canvas
// and its file store. It checks every second or two only while the library has work queued.
importScripts('config.js'); // STUDY_STASH = {app, key, canvas, files, protocol}, written by Study Stash

const MAX_BYTES = 40 * 1024 * 1024;
// What this copy of the extension does, told to the library on every visit (docs/canvas.md, "The extension"):
// 2 = says when Chrome is signed out, passes on Canvas's rate limit, never hands over an error page or an
// oversized file as a file, reloads itself before taking work when its folder is newer, posts files one at a time.
const PROTOCOL = 2;
// Canvas sent us to its sign-in page, or answered as if nobody were signed in. Canvas's other 401,
// {"status":"unauthorized"}, only means this student can't see that part of the course.
const SIGN_IN = /\/login(\/|\?|$)/;
const UNAUTHENTICATED = /unauthenticated|user authorization required/i;

// Canvas itself, and the hosts Canvas keeps file bodies on (a download redirects there).
function allowed(url) {
  if (url.startsWith(STUDY_STASH.canvas + '/')) return true;
  if (!Array.isArray(STUDY_STASH.files)) return /^https:\/\/[a-z0-9.-]+\.inscloudgate\.net\//.test(url); // a config.js from before 1.3
  let u;
  try { u = new URL(url); } catch (e) { return false; }
  if (u.protocol !== 'https:' || u.username || u.password || u.port) return false;
  return STUDY_STASH.files.some(h => h.startsWith('*.') ? u.hostname.endsWith(h.slice(1)) : u.hostname === h);
}

async function app(path, body) {
  const r = await fetch(STUDY_STASH.app + path, {
    method: body ? 'POST' : 'GET',
    headers: {'X-Study-Stash-Key': STUDY_STASH.key, 'Content-Type': 'application/json'},
    body: body ? JSON.stringify(body) : undefined,
  });
  if (!r.ok) throw new Error(`${path}: ${r.status}`);
  return r.json();
}

function b64(buf) {
  const bytes = new Uint8Array(buf);
  let s = '';
  for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
  return btoa(s);
}

// Some files (often your own submissions) don't download through Canvas's /files/<id>/download redirect from a
// service worker. Canvas's API hands out a signed link to the same file, which does.
async function viaPublicUrl(url) {
  const m = url.match(/\/files\/(\d+)\/download/);
  if (!m) throw new Error('not a Canvas file');
  const t = await (await fetch(`${STUDY_STASH.canvas}/api/v1/files/${m[1]}/public_url`, {credentials: 'include'})).text();
  const signed = JSON.parse(t.replace(/^while\(1\);/, '')).public_url;
  if (!signed || !allowed(signed)) throw new Error('no public link');
  return fetch(signed);
}

async function run(job) {
  if (!allowed(job.url)) return {id: job.id, error: 'refused: not a Canvas URL'};
  try {
    const headers = job.kind === 'json' ? {Accept: 'application/json'} : {};
    let r;
    try {
      r = await fetch(job.url, {credentials: 'include', headers});
    } catch (e) {
      if (job.kind !== 'bytes') throw e;
      r = await viaPublicUrl(job.url);
    }
    const h = name => r.headers.get(name) || '';
    const out = {id: job.id, status: r.status, link: h('link'), type: h('content-type'), final: r.url};
    // How much of Canvas's allowance is left, and how long it asked us to wait: the library slows down.
    if (h('x-rate-limit-remaining')) out.rate = h('x-rate-limit-remaining');
    if (h('retry-after')) out.retry_after = h('retry-after');
    // Status 401 as well, for a library from before signed_out.
    if (r.url && SIGN_IN.test(new URL(r.url).pathname)) return {...out, status: 401, signed_out: true};
    if (job.kind === 'bytes' && r.ok) {
      // Canvas says how big a file is before sending it: a file too big to hand over is never read.
      if (Number(h('content-length')) > MAX_BYTES) return {...out, error: 'too big'};
      const buf = await r.arrayBuffer();
      if (buf.byteLength > MAX_BYTES) return {...out, error: 'too big'};
      return {...out, b64: b64(buf)};
    }
    const text = await r.text();
    if (job.kind === 'json' && r.ok && text.trimStart().startsWith('<')) return {...out, status: 401, signed_out: true}; // a sign-in page, not data
    if (r.status === 401 && UNAUTHENTICATED.test(text)) out.signed_out = true;
    // A file Canvas refused: its words, so the library can tell why, never saved as the file.
    if (job.kind === 'bytes') return {...out, text: text.slice(0, 4000)};
    return {...out, text: job.kind === 'json' ? text.replace(/^while\(1\);/, '') : text.slice(0, 2000000)};
  } catch (e) {
    return {id: job.id, error: String(e)};
  }
}

// Study Stash rewrites this folder when it updates: when the manifest there isn't the one running, reload to pick up
// the new files. Checked before asking for work, so a reload never drops work already taken.
async function newerOnDisk() {
  try {
    const onDisk = await (await fetch(chrome.runtime.getURL('manifest.json'), {cache: 'no-store'})).json();
    return !!onDisk.version && onDisk.version !== chrome.runtime.getManifest().version;
  } catch (e) {
    return false;
  }
}

let running = false;
async function pump(force) {
  if (running) return;
  running = true;
  try {
    let idle = 0;
    for (let round = 0; round < 5000; round++) {
      if (await newerOnDisk()) { chrome.runtime.reload(); return; }
      let work;
      try {
        work = await app('/api/v2/canvas/work?v=' + chrome.runtime.getManifest().version + '&p=' + PROTOCOL
                         + (force && round === 0 ? '&force=1' : ''));
      } catch (e) { return; }
      if (work.jobs.length) {
        idle = 0;
        const results = await Promise.all(work.jobs.map(run));
        // Answers together, but each file on its own: one big file per request stays under the library's limit.
        const files = results.filter((_, i) => work.jobs[i].kind === 'bytes');
        const rest = results.filter((_, i) => work.jobs[i].kind !== 'bytes');
        try {
          if (rest.length) await app('/api/v2/canvas/results', {results: rest});
          for (const f of files) await app('/api/v2/canvas/results', {results: [f]});
        } catch (e) {
          return; // the library didn't take them: it hands those jobs out again in ten minutes
        }
        continue;
      }
      if (!work.hot || ++idle > 60) return;           // nothing queued and no agent exploring: sleep until the alarm
      await new Promise(r => setTimeout(r, 1500));   // an agent is exploring: check again shortly
    }
  } finally {
    running = false;
  }
}

function schedule() { chrome.alarms.create('sync', {periodInMinutes: 1}); }
chrome.runtime.onInstalled.addListener(() => { schedule(); pump(true); });
chrome.runtime.onStartup.addListener(() => { schedule(); pump(false); });
chrome.alarms.onAlarm.addListener(a => { if (a.name === 'sync') pump(false); });
// the toolbar popup asks for a sync
chrome.runtime.onMessage.addListener((msg, _sender, reply) => {
  if (msg && msg.sync) { pump(true); reply({ok: true}); }
});
