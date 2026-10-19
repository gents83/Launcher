const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');

const dashboardPath = path.resolve(__dirname, '../../../src/Launcher.App/wwwroot/index.html');
const dashboard = fs.readFileSync(dashboardPath, 'utf8');

function cssRule(selector) {
  const escapedSelector = selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const match = dashboard.match(new RegExp(`${escapedSelector}\\s*\\{([^}]*)\\}`));
  assert.ok(match, `Expected a CSS rule for ${selector}`);
  return match[1];
}

test('settings dialog keeps actions visible while its fields scroll', () => {
  const dialog = cssRule('.settings-dialog');
  const panel = cssRule('.settings-panel');
  const form = cssRule('#teabox-settings-form');
  const body = cssRule('.settings-body');

  assert.match(dialog, /height:\s*min\(90dvh,\s*780px\)/);
  assert.match(panel, /display:\s*flex/);
  assert.match(panel, /height:\s*100%/);
  assert.match(panel, /min-height:\s*0/);
  assert.match(form, /flex-direction:\s*column/);
  assert.match(form, /min-height:\s*0/);
  assert.match(body, /flex:\s*1\s+1\s+auto/);
  assert.match(body, /min-height:\s*0/);
  assert.match(body, /overflow-y:\s*auto/);
  assert.doesNotMatch(body, /max-height:/);
});

test('masthead is titled Launcher without the LA/ mark', () => {
  const masthead = dashboard.match(/<header class="masthead">([\s\S]*?)<\/header>/)?.[1];
  assert.ok(masthead, 'Expected the dashboard masthead');
  assert.match(masthead, /<h1>Launcher<\/h1>/);
  assert.doesNotMatch(masthead, /LA\//);
  assert.match(dashboard, /<h2 id="action-heading">Project actions<\/h2>/);
});

test('footer reserves a bottom-right label for the running launcher version', () => {
  const footer = dashboard.match(/<footer class="footer">([\s\S]*?)<\/footer>/)?.[1];
  assert.ok(footer, 'Expected the dashboard footer');
  assert.match(footer, /ACTIONS ARE PROJECT-SCOPED · LAUNCH ONLY ON BUTTON CLICK/);
  assert.match(footer, /<span id="launcher-version"><\/span>/);
  assert.match(cssRule('.footer'), /display:\s*flex/);
  assert.match(cssRule('.footer'), /justify-content:\s*space-between/);
});

test('Perforce feedback has its own accessible status region near project selection', () => {
  const workspace = dashboard.match(/<section class="workspace"[\s\S]*?<\/section>/)?.[0];
  assert.ok(workspace, 'Expected the project workspace section');
  assert.match(workspace, /<p id="perforce-login-status" class="notice perforce-status" role="status" aria-live="polite"><\/p>/);
  assert.match(dashboard, /<p id="launch-status" class="notice" role="status" aria-live="polite"><\/p>/);
});

test('footer version renderer runs after the dashboard markup is parsed', () => {
  assert.match(dashboard, /<script src="launcher-version\.js" defer><\/script>/);
});
