(() => {
  const settingFieldNames = [
    'perforceServer',
    'perforceUserName',
    'perforceWorkspace',
    'workspaceRoot',
    'projectFolderName',
    'engineFolder',
    'resolutions'
  ];
  const passwordResolutionByAction = {
    keep: 'keepCurrent',
    branch: 'useBranch',
    projectOptions: 'useProjectOptions',
    replace: 'replace'
  };
  const conflictFields = [
    { name: 'PerforceServer', inputId: 'settings-perforce-server', source: 'perforceServer' },
    { name: 'PerforceUserName', inputId: 'settings-perforce-user-name', source: 'perforceUserName' },
    { name: 'PerforceWorkspace', inputId: 'settings-perforce-workspace', source: 'perforceWorkspace' },
    { name: 'WorkspaceRoot', inputId: 'settings-workspace-root', source: 'workspaceRoot' }
  ];

  function roleForEngineFolder(value) {
    const normalized = typeof value === 'string'
      ? value.trim().replace(/[\\/]+/g, '\\').replace(/\\+$/, '').toLowerCase()
      : '';
    if (normalized === 'src\\engine') return 'programmer';
    if (normalized === 'engine') return 'non-programmer';
    return 'custom';
  }

  function engineFolderForRole(role) {
    if (role === 'programmer') return 'src\\Engine';
    if (role === 'non-programmer') return 'Engine';
    return null;
  }

  function snapshotValueForField(snapshot, field) {
    return snapshot[field.source];
  }

  function canDismissSettingsDialog(isSaving) {
    return !isSaving;
  }

  function buildSettingsSavePayload(projectId, fields, passwordAction) {
    const settings = {};
    const source = fields && typeof fields === 'object' ? fields : {};
    settingFieldNames.forEach(name => {
      if (!Object.prototype.hasOwnProperty.call(source, name)) return;
      if (name === 'resolutions') {
        settings.resolutions = Array.isArray(source.resolutions)
          ? source.resolutions.map(resolution => ({
            field: resolution?.field,
            source: resolution?.source
          }))
          : [];
        return;
      }
      settings[name] = source[name];
    });

    const actionKind = passwordAction && passwordAction.kind;
    settings.passwordResolution = passwordResolutionByAction[actionKind] || 'keepCurrent';
    if (actionKind === 'replace' && typeof passwordAction.newPassword === 'string') {
      settings.newPassword = passwordAction.newPassword;
    }

    return { projectId, settings };
  }

  function attachSettingsDialog({
    button,
    dialog,
    getSelectedProject,
    onActiveChange = () => {},
    onSaved = () => {},
    bridge = typeof window !== 'undefined' ? window.launcherBridge : null
  }) {
    const form = document.getElementById('teabox-settings-form');
    const fields = document.getElementById('teabox-settings-fields');
    const projectName = document.getElementById('settings-project-name');
    const engineRole = document.getElementById('settings-engine-role');
    const engineFolder = document.getElementById('settings-engine-folder');
    const engineFolderField = document.getElementById('settings-engine-folder-field');
    const passwordState = document.getElementById('settings-password-state');
    const passwordOptions = document.getElementById('settings-password-options');
    const passwordInput = document.getElementById('settings-new-password');
    const passwordInputField = document.getElementById('settings-password-input-field');
    const status = document.getElementById('settings-status');
    const saveButton = document.getElementById('settings-save');
    const cancelButton = document.getElementById('settings-cancel');
    const controls = conflictFields.map(field => ({
      ...field,
      input: document.getElementById(field.inputId),
      conflict: document.getElementById(`${field.inputId}-conflict`)
    }));

    let activeProjectId = null;
    let snapshot = null;
    let resolutions = {};
    let requestGeneration = 0;
    let isSaving = false;
    let requiresNamedRole = false;

    function setStatus(message, isError = false) {
      status.textContent = message;
      status.dataset.kind = isError ? 'error' : '';
    }

    function currentPasswordAction() {
      const selected = passwordOptions.querySelector('input[name="settings-password-action"]:checked');
      if (!selected) return null;
      if (selected.value === 'replace') {
        return { kind: 'replace', newPassword: passwordInput.value };
      }
      return { kind: selected.value };
    }

    function readFields() {
      const values = {
        perforceServer: document.getElementById('settings-perforce-server').value,
        perforceUserName: document.getElementById('settings-perforce-user-name').value,
        perforceWorkspace: document.getElementById('settings-perforce-workspace').value,
        workspaceRoot: document.getElementById('settings-workspace-root').value,
        projectFolderName: document.getElementById('settings-project-folder-name').value,
        engineFolder: engineFolder.value
      };
      values.resolutions = Object.entries(resolutions).map(([field, source]) => ({ field, source }));
      return values;
    }

    function hasRequiredValues() {
      const values = readFields();
      const required = [
        values.perforceServer,
        values.perforceUserName,
        values.perforceWorkspace,
        values.workspaceRoot,
        values.projectFolderName,
        values.engineFolder
      ];
      if (required.some(value => typeof value !== 'string' || !value.trim())) return false;
      if (engineRole.value !== 'programmer' && engineRole.value !== 'non-programmer' && engineRole.value !== 'custom') {
        return false;
      }
      if (requiresNamedRole && engineRole.value !== 'programmer' && engineRole.value !== 'non-programmer') {
        return false;
      }
      if (snapshot && controls.some(control => isConflict(snapshotValueForField(snapshot, control)) && !resolutions[control.name])) {
        return false;
      }

      const action = currentPasswordAction();
      if (!action) return false;
      if (action.kind === 'replace' && !action.newPassword.trim()) return false;
      if (snapshot?.passwordState === 'conflict' && action.kind !== 'branch' && action.kind !== 'projectOptions' && action.kind !== 'replace') {
        return false;
      }
      if (snapshot?.passwordState === 'unreadable' && action.kind !== 'replace') return false;
      return true;
    }

    function updateSaveButton() {
      saveButton.disabled = !snapshot || isSaving || !hasRequiredValues();
    }

    function updateDismissControls() {
      cancelButton.disabled = !canDismissSettingsDialog(isSaving);
    }

    function isConflict(value) {
      return value && typeof value === 'object' &&
        value.branchValue !== null && value.branchValue !== undefined &&
        value.projectOptionsValue !== null && value.projectOptionsValue !== undefined &&
        value.branchValue !== value.projectOptionsValue;
    }

    function appendConflictChoice(group, field, source, label, selectedValue) {
      const optionLabel = document.createElement('label');
      optionLabel.className = 'settings-conflict-choice';
      const radio = document.createElement('input');
      radio.type = 'radio';
      radio.name = `settings-source-${field.name}`;
      radio.value = source;
      radio.checked = resolutions[field.name] === source;
      const text = document.createElement('span');
      text.textContent = label;
      optionLabel.append(radio, text);
      group.appendChild(optionLabel);
      radio.addEventListener('change', () => {
        if (!radio.checked) return;
        resolutions[field.name] = source;
        if (source === 'branch' || source === 'projectOptions') {
          field.input.value = selectedValue;
        } else if (source === 'custom') {
          field.input.value = '';
        }
        updateSaveButton();
        if (source === 'custom') field.input.focus();
      });
    }

    function renderConflict(field, value) {
      field.conflict.replaceChildren();
      if (!isConflict(value)) {
        field.conflict.hidden = true;
        delete resolutions[field.name];
        return;
      }

      field.conflict.hidden = false;
      const explanation = document.createElement('p');
      explanation.className = 'settings-conflict-help';
      explanation.textContent = 'Choose which value to keep, or enter a custom value.';
      const choices = document.createElement('div');
      choices.className = 'settings-conflict-choices';
      choices.setAttribute('role', 'radiogroup');
      choices.setAttribute('aria-label', `${field.name} value source`);
      appendConflictChoice(choices, field, 'branch', `Branch: ${value.branchValue}`, value.branchValue);
      appendConflictChoice(choices, field, 'projectOptions', `Project options: ${value.projectOptionsValue}`, value.projectOptionsValue);
      appendConflictChoice(choices, field, 'custom', 'Custom', null);
      field.conflict.append(explanation, choices);
    }

    function addPasswordChoice(kind, labelText, checked = false) {
      const label = document.createElement('label');
      label.className = 'settings-password-choice';
      const radio = document.createElement('input');
      radio.type = 'radio';
      radio.name = 'settings-password-action';
      radio.value = kind;
      radio.checked = checked;
      const text = document.createElement('span');
      text.textContent = labelText;
      label.append(radio, text);
      passwordOptions.appendChild(label);
      radio.addEventListener('change', () => {
        if (!radio.checked) return;
        if (kind !== 'replace') passwordInput.value = '';
        passwordInputField.hidden = kind !== 'replace';
        updateSaveButton();
      });
    }

    function renderPasswordState(state) {
      passwordOptions.replaceChildren();
      passwordInput.value = '';
      passwordInputField.hidden = true;
      if (state === 'missing') {
        passwordState.textContent = 'No saved password.';
        addPasswordChoice('keep', 'Keep current password', true);
      } else if (state === 'saved') {
        passwordState.textContent = 'Saved password: ••••••••';
        addPasswordChoice('keep', 'Keep current password', true);
      } else if (state === 'conflict') {
        passwordState.textContent = 'Saved passwords differ: ••••••••';
        addPasswordChoice('branch', 'Use branch password');
        addPasswordChoice('projectOptions', 'Use project-options password');
      } else {
        passwordState.textContent = 'Saved password is unreadable. Replace it to continue.';
        addPasswordChoice('replace', 'Replace password', true);
        passwordInputField.hidden = false;
      }
      if (state !== 'unreadable') addPasswordChoice('replace', 'Replace password');
      updateSaveButton();
    }

    function populate(snapshotValue) {
      resolutions = {};
      snapshot = snapshotValue;
      requiresNamedRole = snapshotValue.engineFolderRole === 'unselected';
      const customRoleOption = engineRole.querySelector('option[value="custom"]');
      customRoleOption.disabled = requiresNamedRole;
      projectName.textContent = snapshotValue.projectName || '';
      controls.forEach(control => {
        const value = snapshotValueForField(snapshotValue, control);
        control.input.value = value?.value ?? '';
        renderConflict(control, value);
      });
      document.getElementById('settings-project-folder-name').value = snapshotValue.projectFolderName || '';
      engineFolder.value = snapshotValue.engineFolder || '';
      const detectedRole = roleForEngineFolder(snapshotValue.engineFolder);
      engineRole.value = requiresNamedRole || !snapshotValue.engineFolder ? '' : detectedRole;
      engineFolderField.hidden = engineRole.value !== 'custom';
      renderPasswordState(snapshotValue.passwordState);
      fields.disabled = false;
      updateSaveButton();
    }

    function clearPasswordInput() {
      passwordInput.value = '';
    }

    function clearOnClose() {
      requestGeneration++;
      activeProjectId = null;
      snapshot = null;
      resolutions = {};
      isSaving = false;
      requiresNamedRole = false;
      engineRole.querySelector('option[value="custom"]').disabled = false;
      clearPasswordInput();
      form.reset();
      fields.disabled = true;
      engineFolderField.hidden = true;
      passwordInputField.hidden = true;
      passwordOptions.replaceChildren();
      controls.forEach(control => {
        control.conflict.replaceChildren();
        control.conflict.hidden = true;
      });
      projectName.textContent = '';
      passwordState.textContent = '';
      setStatus('');
      updateSaveButton();
      updateDismissControls();
      onActiveChange(false);
    }

    async function loadSelectedProject() {
      const project = getSelectedProject();
      if (!project || project.canConfigureTeaBox !== true || !bridge) return;

      activeProjectId = project.id;
      requestGeneration++;
      const generation = requestGeneration;
      snapshot = null;
      resolutions = {};
      fields.disabled = true;
      saveButton.disabled = true;
      projectName.textContent = project.name || '';
      clearPasswordInput();
      setStatus('Loading TeaBox settings…');
      if (typeof dialog.showModal === 'function' && !dialog.open) dialog.showModal();
      onActiveChange(true);

      try {
        const loaded = await bridge.request('teabox-settings-load', { projectId: activeProjectId });
        if (!dialog.open || generation !== requestGeneration) return;
        populate(loaded);
        setStatus('');
        const firstInput = document.getElementById('settings-perforce-server');
        firstInput.focus({ preventScroll: true });
      } catch {
        if (!dialog.open || generation !== requestGeneration) return;
        clearPasswordInput();
        setStatus('TeaBox settings could not be loaded.', true);
      }
    }

    async function save(event) {
      event.preventDefault();
      if (!activeProjectId || !snapshot || isSaving || !hasRequiredValues()) return;

      const passwordAction = currentPasswordAction();
      const projectId = activeProjectId;
      const payload = buildSettingsSavePayload(projectId, readFields(), passwordAction);
      const generation = requestGeneration;
      isSaving = true;
      updateSaveButton();
      updateDismissControls();
      setStatus('Saving TeaBox settings…');
      try {
        await bridge.request('teabox-settings-save', payload);
        clearPasswordInput();
        isSaving = false;
        updateDismissControls();
        if (!dialog.open || generation !== requestGeneration) return;
        dialog.close('saved');
        onSaved(projectId);
      } catch {
        clearPasswordInput();
        isSaving = false;
        updateDismissControls();
        if (!dialog.open || generation !== requestGeneration) return;
        setStatus('TeaBox settings could not be saved.', true);
        updateSaveButton();
      }
    }

    controls.forEach(field => {
      field.input.addEventListener('input', () => {
        if (!snapshot || !isConflict(snapshotValueForField(snapshot, field))) return;
        resolutions[field.name] = 'custom';
        const customRadio = field.conflict.querySelector(`input[name="settings-source-${field.name}"][value="custom"]`);
        if (customRadio) customRadio.checked = true;
        updateSaveButton();
      });
    });
    button.addEventListener('click', loadSelectedProject);
    form.addEventListener('submit', save);
    cancelButton.addEventListener('click', () => {
      if (canDismissSettingsDialog(isSaving)) dialog.close('cancel');
    });
    dialog.addEventListener('cancel', event => {
      if (!canDismissSettingsDialog(isSaving)) event.preventDefault();
    });
    dialog.addEventListener('close', clearOnClose);
    updateDismissControls();
    engineRole.addEventListener('change', () => {
      if (engineRole.value === 'programmer' || engineRole.value === 'non-programmer') {
        engineFolder.value = engineFolderForRole(engineRole.value);
      } else if (engineRole.value === 'custom') {
        if (roleForEngineFolder(engineFolder.value) !== 'custom') engineFolder.value = '';
      } else {
        engineFolder.value = '';
      }
      engineFolderField.hidden = engineRole.value !== 'custom';
      updateSaveButton();
    });
    engineFolder.addEventListener('input', updateSaveButton);
    passwordInput.addEventListener('input', updateSaveButton);
    form.addEventListener('input', updateSaveButton);
    form.addEventListener('change', updateSaveButton);

    return { loadSelectedProject };
  }

  const teaBoxSettings = {
    buildSettingsSavePayload,
    canDismissSettingsDialog,
    engineFolderForRole,
    roleForEngineFolder,
    snapshotValueForField,
    attachSettingsDialog
  };

  if (typeof module === 'object' && module.exports) {
    module.exports = teaBoxSettings;
  } else if (typeof window !== 'undefined') {
    window.teaBoxSettings = teaBoxSettings;
  }
})();
