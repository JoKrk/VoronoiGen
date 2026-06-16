window.voronoiGen = {




  openBlob: function (bytesBase64, mime) {
    try {
      const bytes = Uint8Array.from(atob(bytesBase64), c => c.charCodeAt(0));
      const blob = new Blob([bytes], { type: mime });
      const url = URL.createObjectURL(blob);
      const w = window.open(url, '_blank');
      // Best-effort revoke later
      setTimeout(() => URL.revokeObjectURL(url), 60_000);
      return !!w;
    } catch {
      return false;
    }
  },

  downloadBlob: function (bytesBase64, mime, filename) {
    try {
      const bytes = Uint8Array.from(atob(bytesBase64), c => c.charCodeAt(0));
      const blob = new Blob([bytes], { type: mime });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = filename;
      document.body.appendChild(a);
      a.click();
      document.body.removeChild(a);
      setTimeout(() => URL.revokeObjectURL(url), 100);
      return true;
    } catch {
      return false;
    }
  },

  trackDxfExport: function (mode) {
    try {
      const body = JSON.stringify({ mode: mode || "unknown" });

      if (navigator.sendBeacon) {
        const blob = new Blob([body], { type: "application/json" });
        if (navigator.sendBeacon("/api/export-count", blob)) {
          return true;
        }
      }

      fetch("/api/export-count", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body,
        keepalive: true
      }).catch(() => {});

      return true;
    } catch {
      return false;
    }
  },

  focusElement: function (id) {
    const element = document.getElementById(id);
    if (element) {
      element.focus();
    }
  }
};
