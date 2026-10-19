const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const versionScriptPath = path.resolve(__dirname, '../../../src/Launcher.App/wwwroot/launcher-version.js');
const { renderLauncherVersion } = require(versionScriptPath);

test('renders the semantic launcher version from the entry-point query', () => {
  const versionElement = { textContent: '' };

  renderLauncherVersion('?launcherVersion=1.0.6', versionElement);

  assert.equal(versionElement.textContent, 'Launcher v1.0.6');
});

test('leaves the footer blank when the entry point has no version', () => {
  const versionElement = { textContent: '' };

  renderLauncherVersion('', versionElement);

  assert.equal(versionElement.textContent, '');
});

test('renders the footer when the deferred browser script runs', () => {
  const versionElement = { textContent: '' };
  const context = {
    window: { location: { search: '?launcherVersion=1.0.6' } },
    document: { getElementById: () => versionElement },
    URLSearchParams
  };

  vm.runInNewContext(fs.readFileSync(versionScriptPath, 'utf8'), context);

  assert.equal(versionElement.textContent, 'Launcher v1.0.6');
});
