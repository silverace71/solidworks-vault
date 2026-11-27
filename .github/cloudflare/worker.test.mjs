// Offline tests for the stable /swvault-invites/download Worker contract.
// Run: node .github/cloudflare/worker.test.mjs
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
const source = readFileSync(new URL('./swvault-installer-r2-worker.js', import.meta.url), 'utf8');
const worker = (await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'))).default;
let manifestMode = 'missing';
globalThis.fetch = async (input, options = {}) => {
  const url = typeof input === 'string' ? input : (input instanceof URL ? input.href : input.url);
  if (url.endsWith('/installer/latest')) return Response.json({version: '1.4.1', size: 4, sha256: 'abcd'});
  if (url.endsWith('/manifest.json')) {
    if (manifestMode === 'missing') return new Response('not found', {status: 404});
    return Response.json({schema: 1, version: '1.4.1', zip: {file: 'SwVault-LYNXFSAE-1.4.1.zip', sha256: manifestMode === 'match' ? 'abcd' : 'wrong', size: 4}, setup: {file: 'SwVault-LYNXFSAE-1.4.1-Setup.exe', size: 4}});
  }
  if (url.includes('downloads.lynxmotorsports.racing')) {
    if (options.headers?.get?.('range')) return new Response('x', {status: 206, headers: {'Content-Range': 'bytes 0-0/4', 'Content-Length': '1'}});
    return new Response(options.method === 'HEAD' ? null : 'test', {status: 200, headers: {'Content-Length': '4'}});
  }
  return new Response('origin', {status: 200});
};

let response = await worker.fetch(new Request('https://vault.lynxmotorsports.racing/swvault-invites/download?format=zip'));
assert.equal(response.headers.get('x-swvault-installer-source'), 'r2');
assert.equal(await response.text(), 'test');

response = await worker.fetch(new Request('https://vault.lynxmotorsports.racing/swvault-invites/download?format=exe&invite=ABCD-EFGH-IJKL', {method: 'HEAD'}));
assert.equal(response.headers.get('x-swvault-installer-source'), 'r2');
assert.match(response.headers.get('content-disposition'), /ABCD-EFGH-IJKL/);

response = await worker.fetch(new Request('https://vault.lynxmotorsports.racing/swvault-invites/download?format=zip', {headers: {range: 'bytes=0-0'}}));
assert.equal(response.status, 206);
assert.equal(await response.text(), 'x');

response = await worker.fetch(new Request('https://vault.lynxmotorsports.racing/swvault-invites/download', {headers: {Accept: 'text/html'}}));
assert.equal(await response.text(), 'origin');

manifestMode = 'mismatch';
response = await worker.fetch(new Request('https://vault.lynxmotorsports.racing/swvault-invites/download?format=zip'));
assert.equal(response.headers.get('x-swvault-installer-source'), 'origin-fallback');

manifestMode = 'match';
response = await worker.fetch(new Request('https://vault.lynxmotorsports.racing/swvault-invites/download?format=zip'));
assert.equal(response.headers.get('x-swvault-installer-source'), 'r2');
console.log('PASS: Worker R2 routing, HEAD, resume, invite filename, HTML, and hash fallback');
