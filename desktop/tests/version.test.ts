import test from 'node:test';
import assert from 'node:assert/strict';
import { compareVersions, fetchLatestRelease, isNewerVersion, normalizeVersion, parseVersion, selectDownload, toUpdateCheck } from '../src/version.ts';

test('normalizes a v-prefixed version', () => {
  assert.equal(normalizeVersion(' v1.2.3 '), '1.2.3');
  assert.deepEqual(parseVersion('v1.2.3')?.core, [1n, 2n, 3n]);
});

test('orders core versions numerically', () => {
  assert.equal(compareVersions('0.2.0', '0.10.0'), -1);
  assert.equal(compareVersions('1.0.0', '1.0.0'), 0);
  assert.equal(compareVersions('2.0.0', '1.99.99'), 1);
});

test('follows semver prerelease precedence', () => {
  assert.equal(compareVersions('1.0.0-alpha', '1.0.0'), -1);
  assert.equal(compareVersions('1.0.0-alpha.1', '1.0.0-alpha.beta'), -1);
  assert.equal(compareVersions('1.0.0-beta.11', '1.0.0-beta.2'), 1);
  assert.equal(compareVersions('1.0.0+build.2', '1.0.0+build.9'), 0);
});

test('never offers a version that is equal, older, or malformed', () => {
  assert.equal(isNewerVersion('0.2.0', '0.2.0'), false);
  assert.equal(isNewerVersion('v0.1.9', '0.2.0'), false);
  assert.equal(isNewerVersion('v0.3.0', '0.2.0'), true);
  assert.equal(isNewerVersion('not-a-version', '0.2.0'), false);
});

test('prefers a setup executable, then a portable zip, then the release page', () => {
  const base = { tag_name: 'v1.0.0', name: null, body: null, html_url: 'https://example.invalid/release', published_at: null, prerelease: false, draft: false };
  assert.deepEqual(selectDownload({ ...base, assets: [
    { name: 'portable.zip', browser_download_url: 'https://example.invalid/portable.zip', size: 1 },
    { name: 'OnbodyMic_1.0.0_x64-setup.exe', browser_download_url: 'https://example.invalid/setup.exe', size: 2 },
  ] }), { url: 'https://example.invalid/setup.exe', label: '下载安装程序' });
  assert.deepEqual(selectDownload({ ...base, assets: [] }), { url: 'https://example.invalid/release', label: '打开发布页面' });
});

test('maps a stable newer release to an update check', () => {
  const latest = {
    tag_name: 'v0.3.1', name: 'v0.3.1', body: 'notes', html_url: 'https://example.invalid/r', published_at: null,
    prerelease: false, draft: false, assets: [{ name: 'OnbodyMic-0.3.1-setup.exe', browser_download_url: 'https://example.invalid/dl', size: 10 }],
  };
  const check = toUpdateCheck(latest, '0.2.0');
  assert.equal(check.available, true);
  assert.equal(check.latestVersion, '0.3.1');
  assert.equal(check.downloadUrl, 'https://example.invalid/dl');
  assert.equal(toUpdateCheck({ ...latest, prerelease: true }, '0.2.0').available, false);
});

test('rejects an unreadable update response', async () => {
  const response = new Response('{}', { status: 200, headers: { 'content-type': 'application/json' } });
  await assert.rejects(fetchLatestRelease('https://example.invalid/latest', async () => response), /无法识别的数据/);
});
