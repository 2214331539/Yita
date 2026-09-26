/* global Zotero */

const PLUGIN_ID = "yita-selection@yita.local";
const BRIDGE_URL = "http://127.0.0.1:38473/v1/selection";
const BRIDGE_HEADER = "zotero-reader-v1";
const AUTOMATIC_SEND_DELAY = 700;

let lastText = "";
let lastSentAt = 0;
let pendingTimer = null;

async function startup() {
	await Zotero.initializationPromise;
	Zotero.Reader.registerEventListener(
		"renderTextSelectionPopup",
		onRenderTextSelectionPopup,
		PLUGIN_ID,
	);
}

function shutdown() {
	if (pendingTimer) {
		clearTimeout(pendingTimer);
		pendingTimer = null;
	}
	Zotero.Reader.unregisterEventListener(
		"renderTextSelectionPopup",
		onRenderTextSelectionPopup,
	);
}

function install() {}

function uninstall() {}

function onRenderTextSelectionPopup(event) {
	const text = normalizeSelection(event?.params?.annotation?.text);
	if (!text) {
		return;
	}

	const document = event.doc;
	if (document && typeof event.append === "function") {
		const button = document.createElement("button");
		button.type = "button";
		button.textContent = "Yita";
		button.title = "Translate this selection with Yita";
		button.style.cssText = [
			"appearance:none",
			"border:0",
			"border-radius:8px",
			"padding:5px 9px",
			"margin:2px",
			"font:600 12px system-ui",
			"color:CanvasText",
			"background:color-mix(in srgb, Canvas 82%, CanvasText 18%)",
			"cursor:pointer",
		].join(";");
		button.addEventListener("click", () => {
			if (pendingTimer) {
				clearTimeout(pendingTimer);
				pendingTimer = null;
			}
			void sendSelection(text, button);
		});
		event.append(button);
	}

	if (pendingTimer) {
		clearTimeout(pendingTimer);
	}
	pendingTimer = setTimeout(() => {
		pendingTimer = null;
		void sendSelection(text, null);
	}, AUTOMATIC_SEND_DELAY);
}

async function sendSelection(text, button) {
	const now = Date.now();
	if (text === lastText && now - lastSentAt < 1500) {
		return;
	}
	lastText = text;
	lastSentAt = now;

	try {
		await Zotero.HTTP.request("POST", BRIDGE_URL, {
			body: JSON.stringify({ text }),
			headers: {
				"Content-Type": "application/json",
				"X-Yita-Bridge": BRIDGE_HEADER,
			},
			timeout: 2500,
		});
		if (button?.isConnected) {
			button.textContent = "Sent";
		}
	}
	catch (error) {
		lastText = "";
		lastSentAt = 0;
		if (button?.isConnected) {
			button.textContent = "App unavailable";
		}
		Zotero.debug(`Yita bridge unavailable: ${error?.name || "request failed"}`);
	}
}

function normalizeSelection(value) {
	return typeof value === "string"
		? value.replace(/\r\n?/g, "\n").trim()
		: "";
}
