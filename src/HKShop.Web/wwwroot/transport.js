document.addEventListener("click", event => {
    const trigger = event.target.closest("[data-use-location]");
    if (!trigger) return;
    const fields = trigger.closest("[data-transport-pin]");
    const message = fields.querySelector("[data-location-message]");
    if (!navigator.geolocation) {
        message.textContent = "Enter the latitude and longitude for this address.";
        return;
    }
    navigator.geolocation.getCurrentPosition(position => {
        fields.querySelector("[name=latitude]").value = position.coords.latitude.toFixed(6);
        fields.querySelector("[name=longitude]").value = position.coords.longitude.toFixed(6);
        message.textContent = "Location added. Save the address to confirm the pin.";
    }, () => {
        message.textContent = "Location is unavailable. Check permissions or enter the coordinates.";
    }, { enableHighAccuracy: true, timeout: 15000 });
});

let stopShipperWorkspace;
function initializeShipperWorkspace() {
    stopShipperWorkspace?.();
    const workspace = document.querySelector("[data-shipper-workspace]");
    if (!workspace) return;

    const form = workspace.querySelector("[data-shipper-presence]");
    const toggle = form.querySelector("[name=onDuty]");
    const message = form.querySelector("[data-shipper-message]");
    let locationTimer;
    let active = true;
    let revision = 0;
    let pendingReport = Promise.resolve();

    async function report(enabled, position) {
        const data = new FormData(form);
        const requestRevision = ++revision;
        data.set("onDuty", enabled ? "true" : "false");
        if (position) {
            data.set("latitude", position.coords.latitude);
            data.set("longitude", position.coords.longitude);
        }
        try {
            const sending = pendingReport.then(() => fetch(form.action, { method: "POST", body: data, redirect: "error" }));
            pendingReport = sending.catch(() => {});
            const result = await sending;
            if (!result.ok) throw new Error();
            if (active && requestRevision === revision) {
                message.textContent = enabled ? "Location shared. Keep this page open while available." : "You are off duty.";
            }
        } catch {
            if (active && requestRevision === revision) message.textContent = "Availability update failed. Check your connection.";
        }
    }

    function locate() {
        if (!toggle.checked || !active) return;
        if (!navigator.geolocation) {
            message.textContent = "Location access is required to receive pickup assignments.";
            return;
        }
        navigator.geolocation.getCurrentPosition(position => {
            if (active && toggle.checked) report(true, position);
        }, () => {
            if (active && toggle.checked) message.textContent = "Allow location access to receive pickup assignments.";
        }, { enableHighAccuracy: true, maximumAge: 10000, timeout: 15000 });
    }

    function updatePresence() {
        clearInterval(locationTimer);
        if (toggle.checked) {
            locate();
            locationTimer = setInterval(locate, 30000);
        } else {
            report(false);
        }
    }

    toggle.addEventListener("change", updatePresence);
    if (toggle.checked) updatePresence();
    const jobTimer = setInterval(async () => {
        if (!active) return;
        try {
            const response = await fetch("/api/eoffice/shipper/state", { cache: "no-store", redirect: "error" });
            if (!response.ok) return;
            const state = await response.json();
            if (active && ((state.assignment ?? "") !== (workspace.dataset.assignment ?? "") ||
                (state.stage ?? "") !== (workspace.dataset.stage ?? "") ||
                String(state.verified) !== workspace.dataset.verified)) location.reload();
        } catch {
            // The next poll retries after a temporary connectivity issue.
        }
    }, 8000);
    stopShipperWorkspace = () => {
        active = false;
        clearInterval(locationTimer);
        clearInterval(jobTimer);
        toggle.removeEventListener("change", updatePresence);
    };
}

initializeShipperWorkspace();
window.Blazor?.addEventListener("enhancedload", initializeShipperWorkspace);
