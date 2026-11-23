// Injected into every page before its own scripts (LPods). Two jobs:
// 1. Provide window.chrome.webview, the host bridge upstream's pages are written against (WebView2 on Windows).
// 2. Adjust what the shared host code reports to what the Linux host can do (see patch below).
(() => {
  const listeners = [];
  const handler = window.webkit?.messageHandlers?.host;
  window.chrome = {
    webview: {
      postMessage: (message) => handler?.postMessage(JSON.stringify(message)),
      addEventListener: (type, listener) => {
        if (type === 'message') {
          listeners.push(listener);
        }
      },
      removeEventListener: (type, listener) => {
        const i = listeners.indexOf(listener);
        if (type === 'message' && i >= 0) {
          listeners.splice(i, 1);
        }
      },
    },
  };

  // Upstream's host reports the AirPods mic as carried by WPods (stereo mic stream, mic modes) and "Disable Hands-Free"
  // as the default. Neither is ported: the mic is the plain Bluetooth headset profile, so the page is told "no stereo
  // mic" (it then shows the Hands-Free-only text and no mic modes) and Hands-Free as enabled.
  const patchCaps = (caps) => {
    if (Array.isArray(caps)) {
      for (const cap of ['noStereoMic']) {
        if (!caps.includes(cap)) {
          caps.push(cap);
        }
      }
    }
  };
  const patchDevice = (device) => {
    patchCaps(device.caps);
    if (device.host) {
      device.host.disableHandsFree = false;
      device.host.hdVoice = false;
    }
  };
  const patch = (m) => {
    switch (m.type) {
      case 'init':
      case 'devices':
        m.devices?.forEach(patchDevice);
        if (m.state) {
          patchCaps(m.state.caps);
        }
        break;
      case 'state':
        if (m.state) {
          patchCaps(m.state.caps);
        }
        break;
      case 'caps':
        patchCaps(m.caps);
        break;
      case 'hostSettings':
        if (m.host) {
          m.host.disableHandsFree = false;
          m.host.hdVoice = false;
        }
        break;
    }
    return m;
  };

  // Called by the host with one message object.
  window.__lpods_deliver = (message) => {
    const data = patch(message);
    for (const listener of listeners.slice()) {
      listener({ data });
    }
  };
})();
