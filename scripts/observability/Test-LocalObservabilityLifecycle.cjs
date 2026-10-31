'use strict';
const fs = require('node:fs');
const path = require('node:path');
const { spawn, spawnSync } = require('node:child_process');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');

const repoRoot = () => path.resolve(__dirname, '../..');
const guard = (args, platform) => args.length === 1 && args[0] === '--help' ? 'help' : args.length ? 'invalid' : platform !== 'win32' ? 'platform' : 'run';
const sameProcess = (a, b) => !!(a && b && a.CreationDate && b.CreationDate && a.ProcessId === b.ProcessId && a.CreationDate === b.CreationDate);
const names = new Set(['positive', 'stop1', 'restart', 'stop2', 'harness']);
function report(results, cleanupForced, manualRecovery = false) {
  return { results: results.map(x => ({ name: names.has(x.name) ? x.name : 'harness', status: x.status === 'PASS' ? 'PASS' : 'FAIL' })), cleanupForced: cleanupForced === true, manualRecovery: manualRecovery === true };
}
function persistReport(dir, results, flags) {
  const output = {...report(results,false,flags.manualRecovery), scratchPath:dir, logsSanitized:flags.logsSanitized === true};
  for (const field of ['helperPid','launcherPid']) if (Number.isSafeInteger(flags[field]) && flags[field] > 0) output[field] = flags[field];
  for (const field of ['helperCreationDate','launcherCreationDate']) if (typeof flags[field] === 'string' && /^\/Date\(\d+\)\/$/.test(flags[field])) output[field] = flags[field];
  fs.writeFileSync(path.join(dir,'results.json'),JSON.stringify(output,null,2));
  return output;
}
function tree(all, root) { const ids = new Set([root]); let changed = true; while (changed) { changed = false; for (const p of all) if (ids.has(p.ParentProcessId) && !ids.has(p.ProcessId)) { ids.add(p.ProcessId); changed = true; } } return all.filter(p => ids.has(p.ProcessId)); }
async function recover({identity, owned, inventory, signal, waitGone, release}) {
  let snapshot = owned.slice();
  try {
    const all = await inventory();
    if (all.some(p => sameProcess(p,identity))) {
      for (const p of tree(all,identity.ProcessId)) if (!snapshot.some(old => sameProcess(old,p))) snapshot.push(p);
    }
  } catch { /* A previously captured snapshot may still be usable. */ }
  try { await signal(); } catch { return {retained:true}; }
  if (!snapshot.length) return {retained:true};
  try {
    if (!await waitGone(snapshot)) return {retained:true};
    await release();
    return {retained:false};
  } catch { return {retained:true}; }
}
module.exports = { guard, repoRoot, sameProcess, report, persistReport, recover };

if (require.main === module) main().catch(() => { console.log(JSON.stringify(report([{name:'harness',status:'FAIL'}], false))); process.exitCode = 1; });

async function main() {
  const mode = guard(process.argv.slice(2), process.platform);
  if (mode === 'help') { console.log('Usage: node Test-LocalObservabilityLifecycle.cjs [--help]'); return; }
  if (mode !== 'run') { console.log(JSON.stringify(report([{name:'harness',status:'FAIL'}], false))); process.exitCode = 1; return; }
  const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
  function ps(command) {
    const p = spawnSync('powershell.exe', ['-NoProfile','-Command',command], {encoding:'utf8',timeout:15000,windowsHide:true});
    if (p.status !== 0) throw Error('inventory');
    return p.stdout.trim();
  }
  const processes = () => { const x = JSON.parse(ps('@(Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId,CreationDate) | ConvertTo-Json -Compress')); return Array.isArray(x) ? x : [x]; };
  const ports = () => JSON.parse(ps('@(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $_.LocalPort -in @(18888,4317,4318) } | Select-Object LocalPort) | ConvertTo-Json -Compress') || '[]');
  async function wait(fn, ms) { const end = Date.now()+ms; while (Date.now()<end) { try { const value = await fn(); if (value) return value; } catch {} await delay(300); } throw Error('timeout'); }
  async function req(url, options={}) { return fetch(url,{...options,signal:AbortSignal.timeout(3000)}); }
  const baseUrl = 'http://127.0.0.1:18888';
  const results = []; let active, scratch; const runs = [];
  try {
    assert.equal(ports().length, 0);
    assert.ok(process.env.LOCALAPPDATA);
    const root = path.join(process.env.LOCALAPPDATA, 'Temp', 'opencode', 'MVP41-lifecycle');
    fs.mkdirSync(root, {recursive:true});
    scratch = fs.mkdtempSync(path.join(root, 'run-'));
    async function start(n) {
      assert.equal(ports().length, 0);
      const dir = path.join(scratch, `cycle${n}`); fs.mkdirSync(dir);
      const nonce = crypto.randomBytes(16).toString('hex'); fs.writeFileSync(path.join(dir,'owner.txt'),nonce,{flag:'wx'});
      const helper = spawn('powershell.exe',['-NoProfile','-File',path.join(__dirname,'Invoke-LifecycleConsole.ps1'),'-Dir',dir,'-Repo',repoRoot(),'-Nonce',nonce],{windowsHide:true,stdio:'ignore'});
      const run = {dir,helper,owned:[]}; active = run; runs.push(run);
      helper.on('error', () => {});
      run.helperIdentity = processes().find(p => p.ProcessId === helper.pid);
      run.pid = Number(await wait(() => fs.existsSync(path.join(dir,'pid.txt')) && fs.readFileSync(path.join(dir,'pid.txt'),'utf8'),15000));
      run.launcherIdentity = processes().find(p => p.ProcessId === run.pid && p.ParentProcessId === helper.pid);
      assert.ok(run.launcherIdentity);
      await wait(async () => (await req(baseUrl+'/login')).status === 200,120000);
      const login = await wait(() => { const f = path.join(dir,'out.txt'); return fs.existsSync(f) && fs.readFileSync(f,'utf8').match(/http:\/\/127\.0\.0\.1:18888\/login\?t=[^\s\x1b]+/)?.[0]; },15000);
      const r = await req(baseUrl+'/api/telemetry/validateToken',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({token:new URL(login).searchParams.get('t')})});
      assert.equal(r.status,200); run.key = (await r.json()).apiKey; assert.ok(run.key);
      const snapshot = tree(processes(),run.pid); assert.ok(snapshot.some(p => sameProcess(p,run.launcherIdentity))); run.owned = snapshot;
      return run;
    }
    async function api(run, endpoint) { const r = await req(baseUrl+endpoint,{headers:{'X-API-Key':run.key}}); assert.equal(r.status,200); return r.json(); }
    async function stop(run) {
      const current = processes();
      const root = current.find(p => p.ProcessId === run.pid);
      if (root && run.owned.some(p => sameProcess(p,root))) for (const p of tree(current,run.pid)) if (!run.owned.some(old => sameProcess(old,p))) run.owned.push(p);
      fs.writeFileSync(path.join(run.dir,'signal'),'');
      const signal = await wait(() => { const f = path.join(run.dir,'signal-result.json'); return fs.existsSync(f) && JSON.parse(fs.readFileSync(f,'utf8')); },10000);
      assert.equal(signal.event,0); assert.equal(signal.success,true);
      await wait(() => { const all = processes(); return !run.owned.some(old => all.some(p => sameProcess(old,p))) && ports().length === 0; },30000);
      fs.writeFileSync(path.join(run.dir,'done'),'');
      await wait(() => run.helper.exitCode !== null || run.helper.signalCode !== null,10000);
      assert.equal(run.helper.exitCode,0);
      const completion = JSON.parse(fs.readFileSync(path.join(run.dir,'helper-result.json'),'utf8'));
      assert.equal(completion.status,'PASS'); assert.equal(completion.retained,false); assert.equal(completion.logsSanitized,true);
      assert.ok(!fs.existsSync(path.join(run.dir,'out.txt')) && !fs.existsSync(path.join(run.dir,'err.txt')));
      active = null;
    }
    const a = await start(1);
    const time = String(BigInt(Date.now())*1000000n), resource = {attributes:[{key:'service.name',value:{stringValue:'mvp41-synthetic'}}]};
    const log = {resourceLogs:[{resource,scopeLogs:[{scope:{name:'mvp41'},logRecords:[{timeUnixNano:time,severityNumber:9,body:{stringValue:'mvp41-lifecycle-marker'}}]}]}]};
    const metric = {resourceMetrics:[{resource,scopeMetrics:[{scope:{name:'mvp41'},metrics:[{name:'mvp41.lifecycle.counter',unit:'1',sum:{aggregationTemporality:2,isMonotonic:true,dataPoints:[{timeUnixNano:time,asInt:'7'}]}}]}]}]};
    for (const [endpoint, body] of [['logs',log],['metrics',metric]]) assert.equal((await req('http://127.0.0.1:4318/v1/'+endpoint,{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify(body)})).status,200);
    await wait(async () => JSON.stringify(await api(a,'/api/telemetry/logs?limit=1000')).includes('mvp41-lifecycle-marker') && (await api(a,'/api/telemetry/resources')).some(x => x.name === 'mvp41-synthetic' && x.hasMetrics),30000);
    results.push({name:'positive',status:'PASS'});
    await stop(a); results.push({name:'stop1',status:'PASS'});
    const b = await start(2);
    assert.equal((await api(b,'/api/telemetry/logs?limit=1000')).totalCount,0); assert.equal((await api(b,'/api/telemetry/resources')).length,0);
    results.push({name:'restart',status:'PASS'});
    await stop(b); results.push({name:'stop2',status:'PASS'});
  } catch {
    results.push({name:'harness',status:'FAIL'});
    // Request normal shutdown only; never kill an unverified or reparented process.
    if (active) {
      try {
        await recover({identity:active.launcherIdentity,owned:active.owned,inventory:processes,
          signal:() => fs.writeFileSync(path.join(active.dir,'signal'),''),
          waitGone:async snapshot => { await wait(() => { const now = processes(); return !snapshot.some(old => now.some(p => sameProcess(old,p))) && ports().length === 0; },30000); return true; },
          release:() => fs.writeFileSync(path.join(active.dir,'done'),'')});
        await wait(() => active.helper.exitCode !== null || active.helper.signalCode !== null,10000);
      } catch {}
    }
  }
  // For manual recovery inspect the reported identities and their current ancestry;
  // never infer ownership from PID alone. Raw scratch may remain while helper lives.
  const manualRecovery = !!active && active.helper.exitCode === null && active.helper.signalCode === null;
  if (manualRecovery) active.helper.unref();
  const logsSanitized = runs.every(run => {
    try { const done = JSON.parse(fs.readFileSync(path.join(run.dir,'helper-result.json'),'utf8')); return done.logsSanitized === true && !fs.existsSync(path.join(run.dir,'out.txt')) && !fs.existsSync(path.join(run.dir,'err.txt')); } catch { return false; }
  });
  if (!logsSanitized && !results.some(x => x.name === 'harness')) results.push({name:'harness',status:'FAIL'});
  const flags = {manualRecovery,logsSanitized,helperPid:active?.helper.pid,helperCreationDate:active?.helperIdentity?.CreationDate,launcherPid:active?.pid,launcherCreationDate:active?.launcherIdentity?.CreationDate};
  let output;
  try { output = scratch ? persistReport(scratch,results,flags) : {...report(results,false,manualRecovery),logsSanitized}; }
  catch { output = report([{name:'harness',status:'FAIL'}],false,manualRecovery); process.exitCode = 1; }
  console.log(JSON.stringify(output));
  if (results.length !== 4 || results.some(x => x.status !== 'PASS')) process.exitCode = 1;
}
