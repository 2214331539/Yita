import AppKit
import ApplicationServices
import Foundation

struct PasteboardEntry: Equatable {
    let type: String
    let data: Data
}

struct PasteboardSnapshot: Equatable {
    let changeCount: Int
    let items: [[PasteboardEntry]]
    var isRestorable: Bool {
        guard changeCount >= 0, changeCount < Int.max, items.count <= 16 else { return false }
        var bytes = 0
        var types = 0
        for item in items {
            guard !item.isEmpty, Set(item.map { $0.type }).count == item.count else { return false }
            for entry in item {
                let name = entry.type.lowercased()
                types += 1
                guard types <= 64, !name.isEmpty, !name.contains("promise"), !name.contains("concealed"), !name.contains("transient"),
                      entry.data.count <= 16 * 1_024 * 1_024 - bytes else { return false }
                bytes += entry.data.count
            }
        }
        return true
    }
}

struct ClipboardActivity: Equatable {
    let keyDown: UInt32
    let leftDown: UInt32
    let rightDown: UInt32
    let otherDown: UInt32
}

struct PasteboardObservation: Equatable {
    let changeCount: Int
    let text: String?
    let types: [[String]]
}

enum PasteboardRestore { case restored, superseded, failed }

@MainActor protocol ClipboardSelectionAccess {
    func canPostEvents() -> Bool
    func hasPressedModifiers() -> Bool
    func activity() -> ClipboardActivity
    func changeCount() -> Int
    func snapshot() throws -> PasteboardSnapshot
    func postCopy(to target: SelectionTarget) throws
    func observe() -> PasteboardObservation
    func restore(_ snapshot: PasteboardSnapshot, ifUnchanged changeCount: Int) -> PasteboardRestore
}

enum ClipboardReadError: Error {
    case permission, unsafeTarget, unsafeSnapshot, modified, input, timeout, noText, textLimit, restore, cancelled
    var code: String {
        switch self {
        case .permission: return "clipboard-permission-denied"
        case .unsafeTarget: return "clipboard-unsafe-target"
        case .unsafeSnapshot: return "clipboard-snapshot-unavailable"
        case .modified: return "clipboard-superseded"
        case .input: return "clipboard-user-input"
        case .timeout: return "clipboard-copy-timeout"
        case .noText: return "clipboard-no-text"
        case .textLimit: return "clipboard-text-limit"
        case .restore: return "clipboard-restore-failed"
        case .cancelled: return "selection-cancelled"
        }
    }
    var result: NativeSelection {
        let failure: String
        switch self {
        case .permission: failure = "permissionDenied"
        case .unsafeTarget: failure = "protectedContent"
        case .modified, .input, .textLimit, .cancelled: failure = "cancelled"
        case .timeout: failure = "timeout"
        case .unsafeSnapshot, .noText, .restore: failure = "clipboardUnavailable"
        }
        return NativeSelection(source: "clipboardFallback", failure: failure, diagnosticCode: code)
    }
}

@MainActor final class ClipboardSelectionReader<AX: AXSelectionAccess, Clipboard: ClipboardSelectionAccess> {
    private let ax: AX
    private let clipboard: Clipboard
    private let now: () -> TimeInterval
    private let pause: () async -> Void
    private let isCancelled: () -> Bool

    init(ax: AX, clipboard: Clipboard,
         now: @escaping () -> TimeInterval = { ProcessInfo.processInfo.systemUptime },
         pause: @escaping () async -> Void = { try? await Task.sleep(nanoseconds: 15_000_000) },
         isCancelled: @escaping () -> Bool = { false }) {
        self.ax = ax
        self.clipboard = clipboard
        self.now = now
        self.pause = pause
        self.isCancelled = isCancelled
    }

    func read(_ request: NativeSelectionRequest) async -> NativeSelection {
        do { return try await readChecked(request) }
        catch let error as SelectionReadError { return .failed(error) }
        catch let error as ClipboardReadError { return error.result }
        catch { return ClipboardReadError.unsafeSnapshot.result }
    }

    private func readChecked(_ request: NativeSelectionRequest) async throws -> NativeSelection {
        guard request.isValid else { throw SelectionReadError.invalidRequest }
        guard !isCancelled() else { throw ClipboardReadError.cancelled }
        guard ax.isTrusted(), clipboard.canPostEvents() else { throw ClipboardReadError.permission }
        guard let target = ax.frontmostTarget(), !ax.isOwnTarget(target) else { throw SelectionReadError.noTarget }
        guard request.foregroundProcessId == nil || request.foregroundProcessId == target.processId,
              request.foregroundApplication == nil || request.foregroundApplication == target.bundleIdentifier else {
            throw SelectionReadError.targetChanged
        }
        let deniedBundles = ["com.apple.Terminal", "com.googlecode.iterm2", "net.kovidgoyal.kitty",
                            "org.alacritty", "org.wezfurlong.wezterm"]
        guard !deniedBundles.contains(target.bundleIdentifier ?? ""), !clipboard.hasPressedModifiers() else {
            throw ClipboardReadError.unsafeTarget
        }
        let safetyDeadline = now() + 0.4
        try ax.prepare(target: target, deadline: safetyDeadline)
        guard let focused = try ax.focusedElement() else { throw ClipboardReadError.unsafeTarget }
        func checkSource() throws {
            guard !isCancelled() else { throw ClipboardReadError.cancelled }
            guard now() < safetyDeadline else { throw SelectionReadError.timeout }
            guard ax.isTrusted() else { throw SelectionReadError.permissionDenied }
            guard ax.frontmostTarget() == target,
                  let current = try ax.focusedElement(), ax.sameElement(current, focused) else {
                throw SelectionReadError.targetChanged
            }
        }
        let hit = request.trigger == .mouseGesture ? try ax.element(at: request.pointer) : nil
        _ = try inspectSelectionPaths(ax, origins: [focused, hit].compactMap({ $0 }), check: checkSource)
        guard try ax.allowsCopy(focused) else { throw ClipboardReadError.unsafeTarget }
        let activity = clipboard.activity()
        let snapshot = try clipboard.snapshot()
        guard snapshot.isRestorable else { throw ClipboardReadError.unsafeSnapshot }
        try checkSource()
        guard clipboard.changeCount() == snapshot.changeCount, clipboard.activity() == activity else {
            throw ClipboardReadError.modified
        }
        guard !clipboard.hasPressedModifiers() else { throw ClipboardReadError.unsafeTarget }
        try clipboard.postCopy(to: target)
        let outcome = await waitForCopy(snapshot: snapshot, target: target, focused: focused, activity: activity)
        if let owned = outcome.owned {
            switch clipboard.restore(snapshot, ifUnchanged: owned) {
            case .restored: break
            case .superseded: return ClipboardReadError.modified.result
            case .failed: return ClipboardReadError.restore.result
            }
        }
        return outcome.result
    }

    private func waitForCopy(snapshot: PasteboardSnapshot, target: SelectionTarget, focused: AX.Element,
        activity: ClipboardActivity) async -> (result: NativeSelection, owned: Int?) {
        let deadline = now() + 0.9
        var owned: Int?
        var stable: PasteboardObservation?
        var stableSince = now()
        var cancellation: NativeSelection?
        // A cancellation still observes the in-flight copy before restoring it.
        while now() < deadline {
            let sequence = clipboard.changeCount()
            let unchangedInput = clipboard.activity() == activity
            if sequence == snapshot.changeCount + 1, unchangedInput { owned = sequence }
            if sequence != snapshot.changeCount, sequence != owned { return (ClipboardReadError.modified.result, nil) }
            if !unchangedInput { return (ClipboardReadError.input.result, owned) }
            if isCancelled() { cancellation = ClipboardReadError.cancelled.result }
            if ax.frontmostTarget() != target { cancellation = NativeSelection.failed(.targetChanged) }
            // AX focus checks use a fresh bounded budget while awaiting the pasteboard.
            if cancellation == nil {
                do {
                    try ax.prepare(target: target, deadline: now() + 0.05)
                    guard ax.isTrusted() else { throw SelectionReadError.permissionDenied }
                    guard let current = try ax.focusedElement(), ax.sameElement(current, focused) else {
                        throw SelectionReadError.targetChanged
                    }
                } catch let error as SelectionReadError { cancellation = .failed(error) }
                catch { cancellation = .failed(.unavailable) }
            }
            if let owned = owned {
                let observation = clipboard.observe()
                guard observation.changeCount == owned, clipboard.changeCount() == owned else {
                    return (ClipboardReadError.modified.result, nil)
                }
                if observation != stable { stable = observation; stableSince = now() }
                if now() - stableSince >= 0.08 {
                    if let cancellation = cancellation { return (cancellation, owned) }
                    guard let text = observation.text, !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                        return (ClipboardReadError.noText.result, owned)
                    }
                    guard text.utf16.count <= 20_000 else { return (ClipboardReadError.textLimit.result, owned) }
                    return (NativeSelection(text: text, source: "clipboardFallback"), owned)
                }
            }
            await pause()
        }
        return (cancellation ?? ClipboardReadError.timeout.result, owned)
    }
}

@MainActor final class SystemClipboardSelectionAccess: ClipboardSelectionAccess {
    private let pasteboard = NSPasteboard.general
    static let injectedEventMarker: Int64 = 0x59495441434F5059
    func canPostEvents() -> Bool { CGPreflightPostEventAccess() }
    func hasPressedModifiers() -> Bool {
        !CGEventSource.flagsState(.hidSystemState).intersection([.maskCommand, .maskControl, .maskShift, .maskAlternate]).isEmpty
    }
    func activity() -> ClipboardActivity {
        ClipboardActivity(keyDown: CGEventSource.counterForEventType(.hidSystemState, eventType: .keyDown),
            leftDown: CGEventSource.counterForEventType(.hidSystemState, eventType: .leftMouseDown),
            rightDown: CGEventSource.counterForEventType(.hidSystemState, eventType: .rightMouseDown),
            otherDown: CGEventSource.counterForEventType(.hidSystemState, eventType: .otherMouseDown))
    }
    func changeCount() -> Int { pasteboard.changeCount }
    func snapshot() throws -> PasteboardSnapshot {
        let before = pasteboard.changeCount
        let items = pasteboard.pasteboardItems ?? []
        guard items.count <= 16, !items.isEmpty || (pasteboard.types ?? []).isEmpty else { throw ClipboardReadError.unsafeSnapshot }
        var totalBytes = 0
        var totalTypes = 0
        var snapshot: [[PasteboardEntry]] = []
        for item in items {
            var entries: [PasteboardEntry] = []
            guard !item.types.isEmpty else { throw ClipboardReadError.unsafeSnapshot }
            for type in item.types {
                totalTypes += 1
                let name = type.rawValue.lowercased()
                guard totalTypes <= 64, !name.contains("promise"), !name.contains("concealed"), !name.contains("transient"),
                      let data = item.data(forType: type), data.count <= 16 * 1_024 * 1_024 - totalBytes else {
                    throw ClipboardReadError.unsafeSnapshot
                }
                totalBytes += data.count
                entries.append(PasteboardEntry(type: type.rawValue, data: data))
            }
            snapshot.append(entries)
        }
        guard pasteboard.changeCount == before else { throw ClipboardReadError.modified }
        return PasteboardSnapshot(changeCount: before, items: snapshot)
    }
    func postCopy(to target: SelectionTarget) throws {
        guard canPostEvents(), !hasPressedModifiers(), let source = CGEventSource(stateID: .privateState),
              let down = CGEvent(keyboardEventSource: source, virtualKey: 8, keyDown: true),
              let up = CGEvent(keyboardEventSource: source, virtualKey: 8, keyDown: false) else {
            throw ClipboardReadError.permission
        }
        for event in [down, up] {
            event.flags = .maskCommand
            event.setIntegerValueField(.eventSourceUserData, value: Self.injectedEventMarker)
            event.postToPid(target.processId)
        }
    }
    func observe() -> PasteboardObservation {
        PasteboardObservation(changeCount: pasteboard.changeCount, text: pasteboard.string(forType: .string),
            types: (pasteboard.pasteboardItems ?? []).map { $0.types.map { $0.rawValue }.sorted() })
    }
    func restore(_ snapshot: PasteboardSnapshot, ifUnchanged expected: Int) -> PasteboardRestore {
        var items: [NSPasteboardItem] = []
        for entries in snapshot.items {
            let item = NSPasteboardItem()
            for entry in entries {
                guard item.setData(entry.data, forType: NSPasteboard.PasteboardType(entry.type)) else { return .failed }
            }
            items.append(item)
        }
        // NSPasteboard has no compare-and-swap; keep this final check adjacent to the write.
        guard pasteboard.changeCount == expected else { return .superseded }
        pasteboard.clearContents()
        return items.isEmpty || pasteboard.writeObjects(items) ? .restored : .failed
    }
}

@MainActor func readNativeSelection(_ request: NativeSelectionRequest, allowClipboardFallback: Bool,
    ownerPID: Int32?, control: HelperRequestControl) async -> NativeSelection {
    let ax = SystemAXSelectionAccess(ownerPID: ownerPID)
    let result = AXSelectionReader(access: ax, isCancelled: { control.isCancelled }).read(request)
    guard allowClipboardFallback, ["empty", "unsupportedApplication"].contains(result.failure),
          result.diagnosticCode != "ax-target-unavailable", !control.isCancelled else { return result }
    return await ClipboardSelectionReader(ax: ax, clipboard: SystemClipboardSelectionAccess(),
        isCancelled: { control.isCancelled }).read(request)
}
