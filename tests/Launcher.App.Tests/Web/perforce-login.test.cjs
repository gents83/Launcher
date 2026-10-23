const assert = require('node:assert/strict');
const test = require('node:test');
const { createPerforceLoginController } = require('../../../src/Launcher.App/wwwroot/perforce-login.js');

function deferred() {
  let resolve;
  let reject;
  const promise = new Promise((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}

function createController() {
  const requests = [];
  const statuses = [];
  const bridge = {
    request(type, payload) {
      const response = deferred();
      requests.push({ type, payload, response });
      return response.promise;
    }
  };
  const controller = createPerforceLoginController(bridge, (message, kind) => {
    statuses.push({ message, kind });
  });
  return { controller, requests, statuses };
}

test('authenticates the initially selected project with only its ID', async () => {
  const { controller, requests, statuses } = createController();

  const login = controller.select({ id: 'project-a', name: 'Project A' });

  assert.equal(requests.length, 1);
  assert.equal(requests[0].type, 'perforce-login');
  assert.deepEqual(requests[0].payload, { projectId: 'project-a' });
  assert.match(statuses[0].message, /Perforce/i);
  assert.equal(statuses[0].kind, '');

  requests[0].response.resolve({ succeeded: true, statusMessage: 'Perforce login succeeded.' });
  await login;
  assert.equal(statuses.at(-1).message, 'Perforce login succeeded.');
});

test('uses a safe success fallback for a whitespace-only success message', async () => {
  const { controller, requests, statuses } = createController();

  const login = controller.select({ id: 'project-a' });
  requests[0].response.resolve({ succeeded: true, statusMessage: ' ' });
  await login;

  assert.equal(statuses.at(-1).message, 'Perforce login succeeded.');
  assert.equal(statuses.at(-1).kind, '');
});

test('sends a new login request when the selected project ID changes', () => {
  const { controller, requests } = createController();

  controller.select({ id: 'project-a' });
  controller.select({ id: 'project-b' });

  assert.deepEqual(requests.map(request => request.payload), [
    { projectId: 'project-a' },
    { projectId: 'project-b' }
  ]);
});

test('does not authenticate again when the selected project ID is unchanged', () => {
  const { controller, requests } = createController();

  controller.select({ id: 'project-a' });
  controller.select({ id: 'project-a' });

  assert.equal(requests.length, 1);
});

test('retries login for the same project and ignores the earlier response', async () => {
  const { controller, requests, statuses } = createController();

  const firstLogin = controller.select({ id: 'project-a' });
  const retry = controller.retry('project-a');

  assert.deepEqual(requests.map(request => request.payload), [
    { projectId: 'project-a' },
    { projectId: 'project-a' }
  ]);

  requests[0].response.resolve({ succeeded: false, statusMessage: 'Stale login failure.' });
  requests[1].response.resolve({ succeeded: true, statusMessage: 'Retry succeeded.' });
  await Promise.all([firstLogin, retry]);

  assert.equal(statuses.at(-1).message, 'Retry succeeded.');
  assert.equal(statuses.at(-1).kind, '');
});

test('authenticates a refresh fallback selection but not an unchanged selection', () => {
  const { controller, requests } = createController();

  controller.select({ id: 'previous-project' });
  controller.select({ id: 'fallback-project' });
  controller.select({ id: 'fallback-project' });

  assert.deepEqual(requests.map(request => request.payload.projectId), [
    'previous-project',
    'fallback-project'
  ]);
});

test('reports a failed login without rejecting or blocking selection', async () => {
  const { controller, requests, statuses } = createController();

  const login = controller.select({ id: 'project-a' });
  requests[0].response.resolve({ succeeded: false, statusMessage: 'Perforce login failed.' });

  await assert.doesNotReject(login);
  assert.equal(statuses.at(-1).message, 'Perforce login failed.');
  assert.equal(statuses.at(-1).kind, 'error');

  controller.select({ id: 'project-b' });
  assert.equal(requests.length, 2);
});

test('ignores a late response for a project that is no longer selected', async () => {
  const { controller, requests, statuses } = createController();

  const firstLogin = controller.select({ id: 'project-a' });
  const secondLogin = controller.select({ id: 'project-b' });
  requests[1].response.resolve({ succeeded: true, statusMessage: 'Project B is authenticated.' });
  await secondLogin;

  requests[0].response.resolve({ succeeded: true, statusMessage: 'Stale project A result.' });
  await firstLogin;

  assert.equal(statuses.at(-1).message, 'Project B is authenticated.');
});

test('shows a safe fallback when the bridge rejects and still permits another selection', async () => {
  const { controller, requests, statuses } = createController();

  const firstLogin = controller.select({ id: 'project-a' });
  requests[0].response.reject(new Error('sensitive native details'));

  await assert.doesNotReject(firstLogin);
  assert.equal(statuses.at(-1).message, 'Perforce login could not be completed.');
  assert.equal(statuses.at(-1).kind, 'error');
  assert.equal(statuses.some(status => status.message.includes('sensitive native details')), false);

  controller.select({ id: 'project-b' });
  assert.equal(requests.length, 2);
});
