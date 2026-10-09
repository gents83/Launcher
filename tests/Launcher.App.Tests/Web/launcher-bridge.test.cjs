const assert = require('node:assert/strict');
const test = require('node:test');
const createLauncherBridge = require('../../../src/Launcher.App/wwwroot/launcher-bridge.js');

class FakeWebView {
  messages = [];

  addEventListener(type, listener) {
    assert.equal(type, 'message');
    this.listener = listener;
  }

  postMessage(message) {
    this.messages.push(message);
  }

  respond(response) {
    this.listener({ data: response });
  }
}

function createBridge() {
  const webview = new FakeWebView();
  return { bridge: createLauncherBridge(webview), webview };
}

function settleWithin(promise, milliseconds = 150) {
  return Promise.race([
    promise,
    new Promise(resolve => setTimeout(() => resolve('still-pending'), milliseconds))
  ]);
}

test('correlates a response with its matching request id', async () => {
  const { bridge, webview } = createBridge();
  const request = bridge.request('catalog');
  const [{ requestId, type }] = webview.messages;
  assert.equal(type, 'catalog');

  webview.respond({ requestId, success: true, payload: { projects: [] }, error: null });

  assert.deepEqual(await request, { projects: [] });
});

test('empty request id errors reject every pending bridge request', async () => {
  const { bridge, webview } = createBridge();
  const requests = [bridge.request('catalog'), bridge.request('refresh')];
  webview.respond({ requestId: '', success: false, payload: null, error: 'oversize' });

  const outcomes = await settleWithin(Promise.allSettled(requests));

  assert.notEqual(outcomes, 'still-pending');
  assert.deepEqual(outcomes.map(outcome => outcome.status), ['rejected', 'rejected']);
  assert.deepEqual(outcomes.map(outcome => outcome.reason.message), ['oversize', 'oversize']);
});

test('unknown request id errors reject every pending bridge request', async () => {
  const { bridge, webview } = createBridge();
  const requests = [bridge.request('catalog'), bridge.request('launch', { projectId: 'p', actionId: 'a' })];
  webview.respond({ requestId: 'not-pending', success: false, payload: null, error: 'invalid request' });

  const outcomes = await settleWithin(Promise.allSettled(requests));

  assert.notEqual(outcomes, 'still-pending');
  assert.deepEqual(outcomes.map(outcome => outcome.status), ['rejected', 'rejected']);
  assert.deepEqual(outcomes.map(outcome => outcome.reason.message), ['invalid request', 'invalid request']);
});

test('does not time out a TeaBox launch before its completion response', async () => {
  const { bridge, webview } = createBridge();
  let settled = false;
  const launch = bridge.request('launch', { projectId: 'p', actionId: 'playlist:daily' });
  launch.finally(() => { settled = true; }).catch(() => {});

  await new Promise(resolve => setTimeout(resolve, 150));
  assert.equal(settled, false);
  webview.respond({
    requestId: webview.messages[0].requestId,
    success: true,
    payload: { testMode: true },
    error: null
  });
  assert.deepEqual(await launch, { testMode: true });
});

test('refresh and launch are exclusive and keep rebuilt controls locked', async () => {
  const { bridge } = createBridge();
  const controls = {
    selector: { disabled: false },
    refresh: { disabled: false },
    buttons: [{ disabled: false }, { disabled: false }]
  };
  const gate = bridge.createBusyGate(isBusy => {
    controls.selector.disabled = isBusy;
    controls.refresh.disabled = isBusy;
    controls.buttons.forEach(button => { button.disabled = isBusy; });
  });
  const deferred = () => {
    let resolve;
    const promise = new Promise(done => { resolve = done; });
    return { promise, resolve };
  };
  const refreshResponse = deferred();
  const rebuiltControls = deferred();
  const finishRefresh = deferred();
  let refreshRuns = 0;
  let launchRuns = 0;

  const refresh = gate.runExclusive(async () => {
    refreshRuns++;
    await refreshResponse.promise;
    controls.buttons = [{ disabled: false }, { disabled: false }];
    gate.syncControls();
    rebuiltControls.resolve();
    await finishRefresh.promise;
    return 'refreshed';
  });
  assert.equal(controls.selector.disabled, true);
  assert.equal(controls.refresh.disabled, true);
  assert.ok(controls.buttons.every(button => button.disabled));

  refreshResponse.resolve();
  await rebuiltControls.promise;
  assert.ok(controls.buttons.every(button => button.disabled), 'buttons rebuilt during refresh stay locked');
  assert.equal(await gate.runExclusive(async () => { launchRuns++; }), false);
  assert.equal(launchRuns, 0, 'launch is rejected while refresh owns the gate');

  finishRefresh.resolve();
  assert.equal(await refresh, 'refreshed');
  assert.equal(controls.selector.disabled, false);
  assert.equal(controls.refresh.disabled, false);
  assert.ok(controls.buttons.every(button => !button.disabled));

  const finishLaunch = deferred();
  const launch = gate.runExclusive(async () => {
    launchRuns++;
    await finishLaunch.promise;
    return 'launched';
  });
  assert.equal(await gate.runExclusive(async () => { refreshRuns++; }), false);
  assert.equal(refreshRuns, 1, 'refresh is rejected while launch owns the gate');
  assert.equal(controls.selector.disabled, true);
  finishLaunch.resolve();
  assert.equal(await launch, 'launched');
  assert.equal(controls.selector.disabled, false);
  assert.equal(controls.refresh.disabled, false);
  assert.ok(controls.buttons.every(button => !button.disabled));

  await assert.rejects(gate.runExclusive(async () => { throw new Error('operation failed'); }), /operation failed/);
  assert.equal(controls.selector.disabled, false, 'the gate releases controls from finally after failure');
});
