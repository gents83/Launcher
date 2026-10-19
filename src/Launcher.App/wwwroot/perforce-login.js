(() => {
  function createPerforceLoginController(bridge, setStatus) {
    let selectedProjectId = null;
    let selectionGeneration = 0;

    function isCurrentSelection(projectId, generation) {
      return projectId === selectedProjectId && generation === selectionGeneration;
    }

    function select(project) {
      const projectId = typeof project?.id === 'string' && project.id.trim()
        ? project.id
        : null;
      if (projectId === selectedProjectId) return Promise.resolve();

      selectedProjectId = projectId;
      const generation = ++selectionGeneration;
      if (!projectId) {
        setStatus('');
        return Promise.resolve();
      }

      setStatus('Logging in to Perforce…', '');
      let loginRequest;
      try {
        loginRequest = bridge.request('perforce-login', { projectId });
      } catch {
        if (isCurrentSelection(projectId, generation)) {
          setStatus('Perforce login could not be completed.', 'error');
        }
        return Promise.resolve();
      }

      return Promise.resolve(loginRequest).then(result => {
        if (!isCurrentSelection(projectId, generation)) return;

        const succeeded = result?.succeeded === true;
        const statusMessage = typeof result?.statusMessage === 'string' && result.statusMessage.trim()
          ? result.statusMessage
          : succeeded
            ? 'Perforce login succeeded.'
            : 'Perforce login could not be completed.';
        setStatus(statusMessage, succeeded ? '' : 'error');
      }).catch(() => {
        if (isCurrentSelection(projectId, generation)) {
          setStatus('Perforce login could not be completed.', 'error');
        }
      });
    }

    return { select };
  }

  if (typeof module === 'object' && module.exports) {
    module.exports = { createPerforceLoginController };
  } else if (typeof window !== 'undefined') {
    window.perforceLogin = { createPerforceLoginController };
  }
})();
