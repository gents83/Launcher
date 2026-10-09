(() => {
  function createLauncherBridge(webview) {
    if (!webview || typeof webview.addEventListener !== 'function' || typeof webview.postMessage !== 'function') {
      throw new Error('The WebView2 message bridge is unavailable.');
    }

    const pendingRequests = new Map();
    let nextRequestNumber = 0;

    webview.addEventListener('message', event => {
      const response = event.data;
      if (!response || typeof response !== 'object') return;

      const requestId = typeof response.requestId === 'string' ? response.requestId : '';
      const pending = requestId ? pendingRequests.get(requestId) : null;
      if (pending) {
        pendingRequests.delete(requestId);
        if (response.success) {
          pending.resolve(response.payload);
        } else {
          pending.reject(new Error(response.error || 'The launcher request failed.'));
        }
        return;
      }

      if (response.success === false) {
        const error = new Error(response.error || 'The launcher request failed.');
        const pending = [...pendingRequests.values()];
        pendingRequests.clear();
        pending.forEach(request => request.reject(error));
      }
    });

    function createBusyGate(setControlsDisabled) {
      if (typeof setControlsDisabled !== 'function') {
        throw new TypeError('A control-state callback is required.');
      }

      let isBusy = false;
      return {
        get isBusy() {
          return isBusy;
        },
        syncControls() {
          setControlsDisabled(isBusy);
        },
        async runExclusive(operation) {
          if (isBusy) return false;
          if (typeof operation !== 'function') {
            throw new TypeError('An operation callback is required.');
          }

          isBusy = true;
          try {
            setControlsDisabled(true);
            return await operation();
          } finally {
            isBusy = false;
            setControlsDisabled(false);
          }
        }
      };
    }

    return {
      request(type, requestFields = {}) {
        return new Promise((resolve, reject) => {
          const requestId = `web-${Date.now()}-${++nextRequestNumber}`;
          pendingRequests.set(requestId, { resolve, reject });
          try {
            webview.postMessage({ ...requestFields, requestId, type });
          } catch (error) {
            pendingRequests.delete(requestId);
            reject(error);
          }
        });
      },
      createBusyGate
    };
  }

  if (typeof module === 'object' && module.exports) {
    module.exports = createLauncherBridge;
  } else {
    window.launcherBridge = createLauncherBridge(window.chrome?.webview);
  }
})();
