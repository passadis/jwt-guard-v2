/* JWT Sentinel SPA — MSAL.js auth + Gate Explainer chat + playground + feed */
(() => {
  const cfg = window.SENTINEL;
  const $ = (id) => document.getElementById(id);

  const msalApp = new msal.PublicClientApplication({
    auth: {
      clientId: cfg.spaClientId,
      authority: `https://login.microsoftonline.com/${cfg.tenantId}`,
      redirectUri: window.location.origin + "/",
    },
    cache: { cacheLocation: "sessionStorage" },
  });

  const scopes = [cfg.apiScope];
  const sessionId = crypto.randomUUID();
  let account = null;

  // ------------------------------------------------------------------ auth

  async function getToken() {
    try {
      const res = await msalApp.acquireTokenSilent({ scopes, account });
      return res.accessToken;
    } catch {
      const res = await msalApp.acquireTokenPopup({ scopes });
      return res.accessToken;
    }
  }

  async function authenticatedFetch(path, options = {}) {
    const token = await getToken();
    return fetch(path, {
      ...options,
      headers: {
        ...(options.headers || {}),
        Authorization: `Bearer ${token}`,
        ...(options.body ? { "Content-Type": "application/json" } : {}),
      },
    });
  }

  async function api(path, options = {}) {
    const res = await authenticatedFetch(path, options);
    if (!res.ok) throw new Error(`${path} -> ${res.status}`);
    return res;
  }

  function setSignedIn(acct) {
    account = acct;
    $("user-chip").textContent = acct.name || acct.username;
    $("user-chip").classList.remove("hidden");
    $("btn-signout").classList.remove("hidden");
    $("btn-signin").classList.add("hidden");
    $("signed-out-note").classList.add("hidden");
    $("workspace").classList.remove("hidden");
    $("btn-enter-gate").classList.remove("hidden");
    loadWhoAmI();
    refreshLogs();
  }

  $("btn-signin").addEventListener("click", async () => {
    const res = await msalApp.loginPopup({ scopes });
    setSignedIn(res.account);
  });

  $("btn-signout").addEventListener("click", async () => {
    await msalApp.logoutPopup({ account });
    window.location.reload();
  });

  msalApp.initialize().then(() => {
    const accounts = msalApp.getAllAccounts();
    if (accounts.length) setSignedIn(accounts[0]);
  });

  // ------------------------------------------------------------ identity + gate

  async function loadWhoAmI() {
    const box = $("whoami-result");
    try {
      const response = await api("/api/whoami");
      box.textContent = JSON.stringify(await response.json(), null, 2);
    } catch (error) {
      box.textContent = "Identity check failed: " + error.message;
    }
  }

  async function enterGate() {
    const dot = $("gate-dot");
    const status = $("gate-status");
    const button = $("btn-enter-gate");
    button.disabled = true;
    status.textContent = "Forwarding your token through the protected listener…";
    try {
      const res = await authenticatedFetch("/api/gate/enter", { method: "POST" });
      const data = await res.json();
      if (res.ok && data.allowed && data.gatewayValidated && data.routingContextConsistent) {
        dot.className = "dot dot-ok";
        status.textContent = "You are in — SentinelGate confirmed the protected request";
        $("gate-identity").textContent =
          `tenantId: ${data.tenantId} · objectId: ${data.objectId}`;
      } else {
        dot.className = "dot dot-bad";
        status.textContent = `Gate entry failed — ${data.classification ?? `HTTP ${res.status}`}`;
        $("gate-identity").textContent = data.message ?? "No structured result was returned.";
      }

      const safeEvidence = {
        classification: data.classification,
        allowed: data.allowed,
        gatewayValidated: data.gatewayValidated,
        routingContextConsistent: data.routingContextConsistent,
        tenantId: data.tenantId,
        objectId: data.objectId,
        observedHttpStatus: data.observedHttpStatus ?? res.status,
        evidence: data.evidence,
        limitation: data.limitation,
        message: data.message,
      };
      try {
        await streamAgent(
          "Explain this observed Enter the Gate result. Treat only the supplied fields as evidence: " +
            JSON.stringify(safeEvidence),
          false,
          "Explaining the observed gate result…");
      } catch (agentError) {
        addMsg("agent", "The gate result was observed, but its Agent explanation failed: " + agentError.message);
      }
    } catch (e) {
      dot.className = "dot dot-bad";
      status.textContent = "Gate entry request failed";
      $("gate-identity").textContent = e.message;
    } finally {
      button.disabled = false;
    }
  }

  $("btn-enter-gate").addEventListener("click", enterGate);

  // ------------------------------------------------------------------ chat

  function addMsg(cls, text) {
    const div = document.createElement("div");
    div.className = `msg ${cls}`;
    div.textContent = text;
    $("chat-log").appendChild(div);
    $("chat-log").scrollTop = $("chat-log").scrollHeight;
    return div;
  }

  // minimal markdown: `code`, **bold** (agent output is text-mostly)
  function renderMd(el, raw) {
    const esc = raw
      .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
    el.innerHTML = esc
      .replace(/`([^`]+)`/g, "<code>$1</code>")
      .replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>");
  }

  async function streamAgent(message, showUser = true, thinking = "Consulting the gate…") {
    if (showUser) addMsg("user", message);
    const agentDiv = addMsg("agent thinking", thinking);

    const res = await api("/api/agent/chat", {
      method: "POST",
      body: JSON.stringify({ sessionId, message }),
    });

    agentDiv.classList.remove("thinking");
    agentDiv.textContent = "";
    let full = "";

    const reader = res.body.getReader();
    const decoder = new TextDecoder();
    let buffer = "";
    for (;;) {
      const { value, done } = await reader.read();
      if (done) break;
      buffer += decoder.decode(value, { stream: true });
      const lines = buffer.split("\n\n");
      buffer = lines.pop();
      for (const line of lines) {
        if (!line.startsWith("data: ")) continue;
        const payload = line.slice(6);
        if (payload === "[DONE]") continue;
        try {
          full += JSON.parse(payload).text;
          renderMd(agentDiv, full);
          $("chat-log").scrollTop = $("chat-log").scrollHeight;
        } catch { /* partial frame */ }
      }
    }
    if (!full) agentDiv.textContent = "(no response)";
  }

  $("chat-form").addEventListener("submit", async (ev) => {
    ev.preventDefault();
    const input = $("chat-text");
    const message = input.value.trim();
    if (!message) return;
    input.value = "";

    try {
      await streamAgent(message);
    } catch (e) {
      addMsg("agent", "Error: " + e.message);
    }
  });

  $("btn-reset").addEventListener("click", async () => {
    await api("/api/agent/reset", {
      method: "POST",
      body: JSON.stringify({ sessionId, message: "" }),
    });
    $("chat-log").innerHTML = "";
    addMsg("agent", "Fresh start. What shall we probe?");
  });

  // ------------------------------------------------------------ playground

  document.querySelectorAll(".btn.scenario").forEach((btn) => {
    btn.addEventListener("click", async () => {
      const box = $("sim-result");
      box.classList.remove("hidden");
      box.textContent = `Running scenario '${btn.dataset.scenario}'…`;
      btn.disabled = true;
      try {
        const res = await api("/api/tools/simulate", {
          method: "POST",
          body: JSON.stringify({ scenario: btn.dataset.scenario }),
        });
        const data = await res.json();
        const cls = data.httpStatus === 200 ? "status-200" : "status-4xx";
        box.innerHTML =
          `<span class="${cls}">HTTP ${data.httpStatus}</span> — ` +
          escapeHtml(data.observedResult ?? data.error ?? "") + "\n\n" +
          escapeHtml(JSON.stringify(data, null, 2));
      } catch (e) {
        box.textContent = "Simulation failed: " + e.message;
      } finally {
        btn.disabled = false;
      }
    });
  });

  $("btn-decode").addEventListener("click", async () => {
    const token = $("decode-input").value.trim();
    if (!token) return;
    const box = $("decode-result");
    box.classList.remove("hidden");
    box.textContent = "Decoding…";
    try {
      const res = await api("/api/agent/evidence/decode", {
        method: "POST",
        body: JSON.stringify({ sessionId, token }),
      });
      box.textContent = JSON.stringify(await res.json(), null, 2);
    } catch (e) {
      box.textContent = "Decode failed: " + e.message;
    } finally {
      $("decode-input").value = "";
    }
  });

  // -------------------------------------------------------------- log feed

  async function refreshLogs() {
    const feed = $("log-feed");
    try {
      const res = await api("/api/tools/logs?minutes=120");
      const data = await res.json();
      if (!data.rows || data.rows.length === 0) {
        feed.textContent = data.note ?? "No log rows yet.";
        return;
      }
      feed.innerHTML = "";
      for (const row of data.rows) {
        const status = row.HttpStatus ?? row.httpStatus_d ?? "?";
        const uri = row.RequestUri ?? row.requestUri_s ?? "";
        const host = row.HostName ?? row.host_s ?? "";
        const time = (row.TimeGenerated ?? "").replace("T", " ").slice(5, 19);
        const cls = status === "200" ? "ok" : /^4/.test(status) ? "denied" : "other";
        const div = document.createElement("div");
        div.className = "log-row";
        div.innerHTML =
          `<span class="log-status ${cls}">${escapeHtml(status)}</span>` +
          `<span class="log-uri" title="${escapeHtml(host + uri)}">${escapeHtml(host + uri)}</span>` +
          `<span class="log-time">${escapeHtml(time)}</span>`;
        feed.appendChild(div);
      }
    } catch (e) {
      feed.textContent = "Log query failed: " + e.message;
    }
  }

  $("btn-refresh-logs").addEventListener("click", refreshLogs);
  setInterval(() => { if (account) refreshLogs(); }, 45000);

  function escapeHtml(s) {
    return String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
  }
})();
