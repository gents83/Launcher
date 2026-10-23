const assert = require('node:assert/strict');
const test = require('node:test');
const {
  buildSettingsSavePayload,
  canDismissSettingsDialog,
  engineFolderForRole,
  roleForEngineFolder,
  snapshotValueForField
} = require('../../../src/Launcher.App/wwwroot/teabox-settings.js');

class FakeElement {
  constructor() {
    this.children = [];
    this.dataset = {};
    this.listeners = new Map();
  }

  addEventListener(type, listener) {
    this.listeners.set(type, listener);
  }

  dispatch(type, event = {}) {
    return this.listeners.get(type)?.(event);
  }

  append(...elements) {
    this.children.push(...elements);
  }

  appendChild(element) {
    this.children.push(element);
  }

  replaceChildren(...elements) {
    this.children = elements;
  }

  querySelector(selector) {
    if (selector === 'option[value="custom"]') return this.customOption;
    if (selector === 'input[name="settings-password-action"]:checked') {
      return this.findCheckedInput(this);
    }
    return null;
  }

  findCheckedInput(element) {
    for (const child of element.children) {
      if (child.type === 'radio' && child.name === 'settings-password-action' && child.checked) return child;
      const checkedInput = this.findCheckedInput(child);
      if (checkedInput) return checkedInput;
    }
    return null;
  }

  setAttribute() {}
  focus() {}
  reset() {}

  showModal() {
    this.open = true;
  }

  close(returnValue) {
    this.open = false;
    this.returnValue = returnValue;
    this.dispatch('close');
  }
}

function createSettingsDialogFixture() {
  const ids = [
    'teabox-settings-form', 'teabox-settings-fields', 'settings-project-name',
    'settings-engine-role', 'settings-engine-folder', 'settings-engine-folder-field',
    'settings-password-state', 'settings-password-options', 'settings-new-password',
    'settings-password-input-field', 'settings-status', 'settings-save', 'settings-cancel',
    'settings-perforce-server', 'settings-perforce-user-name', 'settings-perforce-workspace',
    'settings-workspace-root', 'settings-project-folder-name',
    'settings-perforce-server-conflict', 'settings-perforce-user-name-conflict',
    'settings-perforce-workspace-conflict', 'settings-workspace-root-conflict'
  ];
  const elements = Object.fromEntries(ids.map(id => [id, new FakeElement()]));
  elements['settings-engine-role'].customOption = new FakeElement();
  return { elements, dialog: new FakeElement() };
}

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

test('closes the dialog before notifying that settings were saved', async () => {
  const previousDocument = global.document;
  const { elements, dialog } = createSettingsDialogFixture();
  const snapshot = {
    projectName: 'Project A',
    perforceServer: { value: 'p4.example:1666', branchValue: null, projectOptionsValue: null },
    perforceUserName: { value: 'fixture-user', branchValue: null, projectOptionsValue: null },
    perforceWorkspace: { value: 'fixture-client', branchValue: null, projectOptionsValue: null },
    workspaceRoot: { value: 'D:\\workspace', branchValue: null, projectOptionsValue: null },
    projectFolderName: 'ProjectA',
    engineFolder: 'Engine',
    engineFolderRole: 'non-programmer',
    passwordState: 'saved'
  };
  const requests = [];
  const bridge = {
    request(type, payload) {
      requests.push({ type, payload });
      return Promise.resolve(type === 'perforce-login'
        ? { succeeded: true, statusMessage: 'Perforce login succeeded.' }
        : snapshot);
    }
  };
  const login = require('../../../src/Launcher.App/wwwroot/perforce-login.js')
    .createPerforceLoginController(bridge, () => {});
  const saveNotifications = [];
  global.document = {
    getElementById: id => elements[id],
    createElement: () => new FakeElement()
  };

  try {
    await login.select({ id: 'project-a' });
    const settingsDialog = require('../../../src/Launcher.App/wwwroot/teabox-settings.js').attachSettingsDialog({
      button: new FakeElement(),
      dialog,
      getSelectedProject: () => ({ id: 'project-a', name: 'Project A', canConfigureTeaBox: true }),
      bridge,
      onSaved: projectId => {
        saveNotifications.push({ projectId, dialogOpen: dialog.open });
        login.retry(projectId);
      }
    });

    await settingsDialog.loadSelectedProject();
    await elements['teabox-settings-form'].dispatch('submit', { preventDefault() {} });

    assert.equal(dialog.open, false);
    assert.deepEqual(saveNotifications, [{ projectId: 'project-a', dialogOpen: false }]);
    assert.deepEqual(requests.map(request => request.type), [
      'perforce-login',
      'teabox-settings-load',
      'teabox-settings-save',
      'perforce-login'
    ]);
    assert.deepEqual(requests.at(-1).payload, { projectId: 'project-a' });
  } finally {
    if (previousDocument === undefined) delete global.document;
    else global.document = previousDocument;
  }
});
