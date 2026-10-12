const assert = require('node:assert/strict');
const test = require('node:test');
const {
  buildSettingsSavePayload,
  canDismissSettingsDialog,
  engineFolderForRole,
  roleForEngineFolder,
  snapshotValueForField
} = require('../../../src/Launcher.App/wwwroot/teabox-settings.js');

const editableFields = {
  perforceServer: 'p4.example:1666',
  perforceUserName: 'fixture-user',
  perforceWorkspace: 'fixture-client',
  workspaceRoot: 'D:\\workspace',
  projectFolderName: 'SampleProject',
  engineFolder: 'Engine',
  resolutions: [],
  settingsFilePath: 'D:\\must-not-cross-the-bridge',
  path: 'D:\\also-not-allowed',
  newPassword: 'untrusted-field-value'
};

test('recognizes the two engine folder defaults across slash and case differences', () => {
  assert.equal(roleForEngineFolder('src\\Engine'), 'programmer');
  assert.equal(roleForEngineFolder('SRC/engine/'), 'programmer');
  assert.equal(roleForEngineFolder('Engine'), 'non-programmer');
  assert.equal(roleForEngineFolder('engine/'), 'non-programmer');
});

test('classifies an unrecognized engine folder as custom', () => {
  assert.equal(roleForEngineFolder('C:\\custom\\Engine'), 'custom');
});

test('reads a lower-camel snapshot field using its descriptor source', () => {
  const snapshot = {
    perforceServer: {
      value: 'p4.fixture:1666',
      branchValue: null,
      projectOptionsValue: null
    }
  };
  const field = { name: 'PerforceServer', source: 'perforceServer' };

  assert.deepEqual(snapshotValueForField(snapshot, field), {
    value: 'p4.fixture:1666',
    branchValue: null,
    projectOptionsValue: null
  });
});

test('maps named engine roles to their exact TeaBox folder values', () => {
  assert.equal(engineFolderForRole('programmer'), 'src\\Engine');
  assert.equal(engineFolderForRole('non-programmer'), 'Engine');
});

test('keeps the current password out and allowlists settings sent to the bridge', () => {
  const payload = buildSettingsSavePayload('fixture-project', editableFields, { kind: 'keep' });

  assert.deepEqual(payload, {
    projectId: 'fixture-project',
    settings: {
      perforceServer: 'p4.example:1666',
      perforceUserName: 'fixture-user',
      perforceWorkspace: 'fixture-client',
      workspaceRoot: 'D:\\workspace',
      projectFolderName: 'SampleProject',
      engineFolder: 'Engine',
      resolutions: [],
      passwordResolution: 'keepCurrent'
    }
  });
  assert.equal('newPassword' in payload.settings, false);
  assert.equal('path' in payload, false);
  assert.equal('settingsFilePath' in payload.settings, false);
});

test('includes a password only for an explicit replacement action', () => {
  const payload = buildSettingsSavePayload('fixture-project', editableFields, {
    kind: 'replace',
    newPassword: 'replacement-only-fixture'
  });

  assert.equal(payload.settings.passwordResolution, 'replace');
  assert.equal(payload.settings.newPassword, 'replacement-only-fixture');
  assert.equal('settingsFilePath' in payload.settings, false);
  assert.equal('path' in payload.settings, false);
});

test('projects nested conflict resolutions to only field and source', () => {
  const payload = buildSettingsSavePayload('fixture-project', {
    ...editableFields,
    resolutions: [{
      field: 'PerforceServer',
      source: 'branch',
      settingsFilePath: 'D:\\must-not-cross-the-bridge'
    }]
  }, { kind: 'keep' });

  assert.deepEqual(payload.settings.resolutions, [{
    field: 'PerforceServer',
    source: 'branch'
  }]);
  assert.equal('settingsFilePath' in payload.settings.resolutions[0], false);
});

test('blocks dialog dismissal only while a save is in flight', () => {
  assert.equal(canDismissSettingsDialog(true), false);
  assert.equal(canDismissSettingsDialog(false), true);
});
