# Yita Selection Bridge for Zotero

This companion add-on fixes selection translation in Zotero's built-in PDF reader. Zotero renders PDF text inside its own reader and does not expose the selected range through Windows UI Automation, so the add-on sends the current reader selection to Yita over a loopback-only connection.

## Install

1. Keep the latest Yita running.
2. In Zotero, open **Tools → Plugins**.
3. Open the gear menu, choose **Install Plugin From File…**, and select `Yita-Zotero-Selection.xpi`.
4. Restart Zotero when prompted.

Selecting PDF text then opens the normal Yita popup. The small **Yita** action in Zotero's selection toolbar can also resend the selection.

## Privacy

- The add-on reads only the text currently selected in Zotero's PDF reader.
- It does not read your Zotero library, notes, attachments, account, or credentials.
- The selection is sent only to `127.0.0.1` and is not stored by the add-on.
- Yita's normal DeepSeek and optional AI-history settings still apply.
