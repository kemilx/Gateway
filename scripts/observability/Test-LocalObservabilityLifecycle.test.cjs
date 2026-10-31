const { test } = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const fs = require('node:fs');
const os = require('node:os');
const {spawnSync} = require('node:child_process');
const h = require('./Test-LocalObservabilityLifecycle.cjs');

test('guards arguments and platform', () => {
  assert.equal(h.guard([], 'win32'), 'run');
  assert.equal(h.guard(['--help'], 'linux'), 'help');
  assert.equal(h.guard(['--bad'], 'win32'), 'invalid');
  assert.equal(h.guard([], 'linux'), 'platform');
});
test('repository path is independent of current working directory', () => {
  const previous = process.cwd();
  try {
    process.chdir(path.parse(previous).root);
    assert.equal(h.repoRoot(), path.resolve(__dirname, '../..'));
  } finally { process.chdir(previous); }
});
test('ownership requires identical process identity', () => {
  const p = { ProcessId: 42, CreationDate: 'one' };
  assert.equal(h.sameProcess(p, { ProcessId: 42, CreationDate: 'one' }), true);
  assert.equal(h.sameProcess(p, { ProcessId: 42, CreationDate: 'two' }), false);
  assert.equal(h.sameProcess(p, { ProcessId: 43, CreationDate: 'one' }), false);
});
test('report excludes raw errors and credentials', () => {
  const report = h.report([{ name: 'positive', status: 'FAIL', error: 'secret-token', apiKey: 'secret-key' }], false);
  assert.equal(JSON.stringify(report).includes('secret'), false);
  assert.deepEqual(report.results, [{ name: 'positive', status: 'FAIL' }]);
});
test('persisted report drops unknown fields and includes scratch path', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'mvp41-test-'));
  try {
    h.persistReport(dir, [{name:'positive',status:'FAIL',error:'secret'}], {logsSanitized:true, unexpected:'secret'});
    const data = JSON.parse(fs.readFileSync(path.join(dir,'results.json'),'utf8'));
    assert.equal(data.scratchPath,dir);
    assert.equal(data.logsSanitized,true);
    assert.equal(JSON.stringify(data).includes('secret'),false);
  } finally { fs.rmSync(dir,{recursive:true,force:true}); }
});
test('helper rejects foreign directory without removing files', {skip:process.platform !== 'win32'}, () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'mvp41-foreign-'));
  try {
    const file = path.join(dir,'out.txt'); fs.writeFileSync(file,'keep');
    const p = spawnSync('powershell.exe',['-NoProfile','-File',path.join(__dirname,'Invoke-LifecycleConsole.ps1'),'-Dir',dir,'-Repo',h.repoRoot(),'-Nonce','a'.repeat(32)],{encoding:'utf8',timeout:10000});
    assert.notEqual(p.status,0); assert.equal(fs.readFileSync(file,'utf8'),'keep');
  } finally { fs.rmSync(dir,{recursive:true,force:true}); }
});
test('recovery refuses release when startup root vanished without snapshot', async () => {
  const calls = [];
  const result = await h.recover({identity:{ProcessId:10,CreationDate:'one'},owned:[],inventory:async()=>{calls.push('inventory'); return [{ProcessId:11,ParentProcessId:10,CreationDate:'two'}];},signal:async()=>calls.push('signal'),waitGone:async()=>{calls.push('wait'); return true;},release:async()=>calls.push('done')});
  assert.deepEqual(calls,['inventory','signal']); assert.equal(result.retained,true);
});
test('recovery retains helper when captured child survives', async () => {
  const calls=[]; const root={ProcessId:10,CreationDate:'one'}, child={ProcessId:11,ParentProcessId:10,CreationDate:'two'};
  const result=await h.recover({identity:root,owned:[],inventory:async()=>{calls.push('inventory'); return [root,child];},signal:async()=>calls.push('signal'),waitGone:async snapshot=>{calls.push('wait'); assert.deepEqual(snapshot,[root,child]); return false;},release:async()=>calls.push('done')});
  assert.deepEqual(calls,['inventory','signal','wait']); assert.equal(result.retained,true);
});
test('recovery releases helper only after captured tree exits', async () => {
  const calls=[]; const root={ProcessId:10,CreationDate:'one'}, child={ProcessId:11,ParentProcessId:10,CreationDate:'two'};
  const result=await h.recover({identity:root,owned:[],inventory:async()=>{calls.push('inventory'); return [root,child];},signal:async()=>calls.push('signal'),waitGone:async snapshot=>{calls.push('wait'); assert.deepEqual(snapshot,[root,child]); return true;},release:async()=>calls.push('done')});
  assert.deepEqual(calls,['inventory','signal','wait','done']); assert.equal(result.retained,false);
});
