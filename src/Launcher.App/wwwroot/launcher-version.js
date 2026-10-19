(() => {
  function renderLauncherVersion(search, element) {
    if (!element) return;
    const version = new URLSearchParams(search).get('launcherVersion');
    if (!version) return;

    element.textContent = `Launcher v${version}`;
  }

  if (typeof module === 'object' && module.exports) {
    module.exports = { renderLauncherVersion };
  } else if (typeof window !== 'undefined' && typeof document !== 'undefined') {
    renderLauncherVersion(window.location.search, document.getElementById('launcher-version'));
  }
})();
