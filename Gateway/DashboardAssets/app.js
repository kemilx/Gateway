"use strict";

(() => {
  const byId = (id) => document.getElementById(id);
  const number = new Intl.NumberFormat("es-ES", { maximumFractionDigits: 2 });
  const date = new Intl.DateTimeFormat("es-ES", { dateStyle: "medium", timeStyle: "medium" });
  const outcomes = [
    ["inspected", "Inspeccionadas"],
    ["blocked", "Bloqueadas por fallo de inspección"],
    ["capacity_exceeded", "Capacidad excedida"],
    ["canceled", "Canceladas"],
    ["write_error", "Error de escritura"],
    ["faulted", "Fallo interno"],
    ["timed_out", "Tiempo agotado"],
    ["policy_blocked", "Bloqueadas por política"]
  ];
  const reloadReasons = {
    read_failed: "No se pudo leer el archivo de políticas.",
    too_large: "La actualización supera el tamaño permitido.",
    invalid_configuration: "La actualización no es válida.",
    incompatible_snapshot: "La actualización no cumple las restricciones de la instancia."
  };
  let inFlight = null;
  let pollTimer = null;
  let lastReceived = null;
  let demoMode = "Mask";

  const setText = (id, value) => { byId(id).textContent = value; };
  const count = (value) => typeof value === "number" && Number.isFinite(value) && value >= 0 && Number.isInteger(value);
  const text = (value) => typeof value === "string" || count(value);
  const validDate = (value) => typeof value === "string" && Number.isFinite(Date.parse(value));

  function validUtcDate(value) {
    return typeof value === "string"
      && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$/.test(value)
      && validDate(value) && new Date(value).toISOString().slice(0, 19) === value.slice(0, 19);
  }

  function validReload(data) {
    if (!Object.hasOwn(data, "policyReload")) return true;
    const r = data.policyReload;
    if (!r || typeof r !== "object" || Array.isArray(r)) return false;
    if (r.enabled === false) {
      return r.status === "disabled" && r.reason === null
        && r.lastCheckedAtUtc === null && r.lastAppliedAtUtc === null;
    }
    return r.enabled === true && validUtcDate(r.lastCheckedAtUtc) && validUtcDate(r.lastAppliedAtUtc)
      && ((r.status === "valid" && r.reason === null)
        || (r.status === "rejected" && typeof r.reason === "string" && Object.hasOwn(reloadReasons, r.reason)));
  }

  function validRules(p) {
    const hasCount = Object.hasOwn(p, "ruleCount");
    const hasTypes = Object.hasOwn(p, "transformations");
    if (!hasCount && !hasTypes) return true;
    return hasCount && hasTypes && count(p.ruleCount) && p.ruleCount >= 1 && p.ruleCount <= 16
      && Array.isArray(p.transformations) && p.transformations.length >= 1
      && p.transformations.every((t) => ["PAN", "Email", "Phone"].includes(t))
      && new Set(p.transformations).size === p.transformations.length
      && p.transformations.includes(p.transformation) && p.ruleCount >= p.transformations.length;
  }

  function validate(data) {
    const m = data?.metrics;
    return data?.schemaVersion === 1 && validDate(data.startedAtUtc) && validReload(data)
      && typeof data.health?.live === "boolean" && typeof data.health?.ready === "boolean"
      && Array.isArray(data.policies) && data.policies.every((p) => p &&
        typeof p.routeId === "string" && typeof p.policyId === "string" && text(p.version)
        && typeof p.mode === "string" && ["PAN", "Email", "Phone"].includes(p.transformation) && count(p.maxBodyBytes) && validRules(p))
      && m && outcomes.every(([key]) => count(m.outcomes?.[key]))
      && [m.validatedMasks, m.activeInspections, m.reservedBodyBytes].every(count)
      && [m.input, m.output].every((v) => v && count(v.samples) && count(v.totalBytes))
      && count(m.duration?.samples) && typeof m.duration?.totalMilliseconds === "number"
      && Number.isFinite(m.duration.totalMilliseconds) && m.duration.totalMilliseconds >= 0;
  }

  function cell(value, className) {
    const element = document.createElement("td");
    element.textContent = value;
    if (className) element.className = className;
    return element;
  }

  function render(data) {
    const m = data.metrics;
    for (const [id, healthy, yes, no] of [
      ["live", data.health.live, "En funcionamiento", "No disponible"],
      ["ready", data.health.ready, "Lista", "No lista"]
    ]) {
      setText(id, healthy ? yes : no);
      byId(id).className = `health-value ${healthy ? "healthy" : "unhealthy"}`;
    }
    setText("started-at", date.format(new Date(data.startedAtUtc)));
    renderReload(data);
    setText("active", number.format(m.activeInspections));
    setText("reserved", `${number.format(m.reservedBodyBytes)} B`);
    setText("masks", number.format(m.validatedMasks));
    byId("outcomes").replaceChildren(...outcomes.map(([key, label]) => {
      const row = document.createElement("tr");
      const reference = document.createElement("code");
      reference.textContent = key;
      const referenceCell = document.createElement("td");
      referenceCell.append(reference);
      row.append(cell(label), cell(number.format(m.outcomes[key]), "numeric"), referenceCell);
      return row;
    }));
    for (const key of ["input", "output"]) {
      setText(`${key}-total`, `${number.format(m[key].totalBytes)} B`);
      setText(`${key}-samples`, `Muestras: ${number.format(m[key].samples)}`);
    }
    setText("duration-mean", m.duration.samples > 0
      ? `${number.format(m.duration.totalMilliseconds / m.duration.samples)} ms` : "Sin muestras");
    setText("duration-samples", `Muestras: ${number.format(m.duration.samples)}`);
    setText("duration-total", `Total acumulado: ${number.format(m.duration.totalMilliseconds)} ms`);
    const rows = data.policies.map((p) => {
      const row = document.createElement("tr");
      const ruleCount = p.ruleCount ?? 1;
      const transformations = p.transformations ?? [p.transformation];
      const rules = `${transformations.join(", ")} · ${ruleCount} ${ruleCount === 1 ? "regla" : "reglas"}`;
      row.append(...[p.routeId, p.policyId, p.version, p.mode, rules, number.format(p.maxBodyBytes)].map((v) => cell(v)));
      return row;
    });
    if (!rows.length) {
      const row = document.createElement("tr");
      const empty = cell("No hay políticas publicadas por esta instancia.");
      empty.colSpan = 6;
      row.append(empty);
      rows.push(row);
    }
    byId("policies").replaceChildren(...rows);
  }

  function renderReload(data) {
    const available = Object.hasOwn(data, "policyReload");
    const r = data.policyReload;
    const rejected = available && r.status === "rejected";
    setText("reload-status", !available ? "Estado de recarga no disponible en este servidor"
      : r.status === "disabled" ? "Recarga desactivada"
        : rejected ? "Actualización rechazada" : "Última comprobación válida");
    byId("reload-dates").hidden = !available;
    setText("reload-checked-at", available && r.lastCheckedAtUtc !== null
      ? date.format(new Date(r.lastCheckedAtUtc)) : "Sin comprobaciones");
    setText("reload-applied-at", available && r.lastAppliedAtUtc !== null
      ? date.format(new Date(r.lastAppliedAtUtc)) : "Sin activaciones por recarga");
    byId("reload-rejection").hidden = !rejected;
    byId("reload-reason").hidden = !rejected;
    setText("reload-reason", rejected ? reloadReasons[r.reason] : "");
  }

  function schedule() {
    clearTimeout(pollTimer);
    if (byId("auto-refresh").checked && !document.hidden && !inFlight) {
      pollTimer = setTimeout(() => refresh(false), 5000);
    }
  }

  async function refresh(manual) {
    if (inFlight || document.hidden) return;
    clearTimeout(pollTimer);
    const controller = new AbortController();
    inFlight = controller;
    const timeout = setTimeout(() => controller.abort(), 8000);
    byId("refresh").disabled = true;
    byId("summary").setAttribute("aria-busy", "true");
    setText("request-state", lastReceived ? "Actualizando… Se conserva la última captura." : "Cargando el resumen…");
    if (manual) setText("announcement", "Actualizando el resumen.");
    try {
      const response = await fetch("/dashboard/api/summary", {
        method: "GET", mode: "same-origin", credentials: "same-origin", redirect: "error",
        cache: "no-store", signal: controller.signal, headers: { Accept: "application/json" }
      });
      if (!response.ok) throw new Error("summary unavailable");
      const data = await response.json();
      if (controller.signal.aborted) throw new Error("request canceled");
      if (!validate(data)) throw new Error("invalid summary");
      render(data);
      lastReceived = new Date();
      byId("error-notice").hidden = true;
      setText("request-state", "Captura recibida · solo consulta");
      setText("updated-at", `Última recepción: ${date.format(lastReceived)}`);
      if (manual) setText("announcement", "Resumen actualizado.");
    } catch {
      byId("error-notice").hidden = false;
      setText("request-state", lastReceived ? "Datos anteriores · actualización pendiente" : "Sin datos disponibles");
      setText("error-detail", lastReceived
        ? "Se conserva la última captura, que puede estar desactualizada. Pulsa Actualizar para reintentar."
        : "No se ha recibido una captura válida. Comprueba el servicio y pulsa Actualizar para reintentar.");
      if (manual) setText("announcement", lastReceived
        ? "No se pudo actualizar. Se conservan datos anteriores."
        : "No se pudo cargar el resumen. Puedes reintentar con Actualizar.");
    } finally {
      clearTimeout(timeout);
      inFlight = null;
      byId("refresh").disabled = false;
      byId("summary").setAttribute("aria-busy", "false");
      schedule();
    }
  }

  byId("refresh").addEventListener("click", () => refresh(true));
  byId("auto-refresh").addEventListener("change", schedule);
  document.addEventListener("visibilitychange", () => {
    clearTimeout(pollTimer);
    if (document.hidden) {
      if (inFlight) inFlight.abort();
      else setText("request-state", lastReceived ? "Actualización pausada · se conserva la última captura" : "Actualización pausada");
    } else {
      if (lastReceived) setText("request-state", "Última captura conservada · pulsa Actualizar para renovarla");
      schedule();
    }
  });

  for (const mode of ["Mask", "Block"]) {
    byId(`demo-${mode.toLowerCase()}`).addEventListener("click", () => {
      demoMode = mode;
      byId("demo-mask").setAttribute("aria-pressed", String(mode === "Mask"));
      byId("demo-block").setAttribute("aria-pressed", String(mode === "Block"));
      setText("demo-result", "Sin ejecutar. Selección local, no configuración real.");
    });
  }
  byId("demo-run").addEventListener("click", () => {
    setText("demo-result", demoMode === "Mask" ? "[REDACTED]" : "502 · privacy_response_blocked");
  });

  if (document.hidden) {
    byId("summary").setAttribute("aria-busy", "false");
    setText("request-state", "Actualización pausada · pulsa Actualizar al volver");
  } else {
    refresh(false);
  }
})();
