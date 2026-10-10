import Foundation

@MainActor final class FixtureClipboardAccess: ClipboardSelectionAccess {
    var permitted = true
    var modifiers = false
    var clock: TimeInterval = 0
    var sequence = 40
    var keyDown: UInt32 = 0
    var items = [[PasteboardEntry(type: "public.utf8-plain-text", data: Data("original".utf8)),
                  PasteboardEntry(type: "public.html", data: Data("<b>original</b>".utf8))],
                 [PasteboardEntry(type: "test.opaque", data: Data([0, 1, 255]))]]
    var text: String? = "original"
    var copiedText: String? = "copied selection"
    var copyDelay: TimeInterval = 0.03
    var neverCopies = false
    var copyIncrements = 1
    var snapshotError = false
    var postError = false
    var restoreError = false
    var supersedeOnRestore = false
    var postedAt: TimeInterval?
    var copied = false
    var posts = 0
    var restores = 0
    var observations = 0
    var restored = false
    var onSnapshot: (() -> Void)?
    var onAdvance: (() -> Void)?

    func canPostEvents() -> Bool { permitted }
    func hasPressedModifiers() -> Bool { modifiers }
    func activity() -> ClipboardActivity { ClipboardActivity(keyDown: keyDown, leftDown: 0, rightDown: 0, otherDown: 0) }
    func changeCount() -> Int { sequence }
    func snapshot() throws -> PasteboardSnapshot {
        if snapshotError { throw ClipboardReadError.unsafeSnapshot }
        let result = PasteboardSnapshot(changeCount: sequence, items: items)
        onSnapshot?()
        return result
    }
    func postCopy(to target: SelectionTarget) throws {
        if postError { throw ClipboardReadError.permission }
        posts += 1
        postedAt = clock
    }
    func advance(to time: TimeInterval? = nil) {
        clock = time ?? (clock + 0.015)
        if let start = postedAt, !copied, !neverCopies, clock - start >= copyDelay {
            copied = true
            sequence += copyIncrements
            text = copiedText
            items = [[PasteboardEntry(type: "public.utf8-plain-text", data: Data((copiedText ?? "").utf8))]]
        }
        onAdvance?()
    }
    func observe() -> PasteboardObservation {
        observations += 1
        return PasteboardObservation(changeCount: sequence, text: text, types: items.map { $0.map { $0.type } })
    }
    func restore(_ snapshot: PasteboardSnapshot, ifUnchanged expected: Int) -> PasteboardRestore {
        restores += 1
        if supersedeOnRestore { userCopy() }
        guard sequence == expected else { return .superseded }
        if restoreError { return .failed }
        items = snapshot.items
        text = snapshot.items.first?.first.flatMap { String(data: $0.data, encoding: .utf8) }
        sequence += 1
        restored = true
        return .restored
    }
    func userCopy() {
        sequence += 1
        text = "user copied"
        items = [[PasteboardEntry(type: "public.utf8-plain-text", data: Data("user copied".utf8))]]
    }
}

@MainActor func readClipboardFixture(_ name: String, request: NativeSelectionRequest,
    allowClipboardFallback: Bool, control: HelperRequestControl) async -> NativeSelection {
    let ax = FixtureAXAccess()
    ax.elements[1]?.text = nil
    ax.elements[1]?.range = nil
    let result = AXSelectionReader(access: ax, isCancelled: { control.isCancelled }).read(request)
    guard allowClipboardFallback, ["empty", "unsupportedApplication"].contains(result.failure) else { return result }
    let clipboard = FixtureClipboardAccess()
    clipboard.clock = ProcessInfo.processInfo.systemUptime
    clipboard.copyDelay = name == "clipboard-slow" ? 0.45 : 0.03
    // IPC deadlines and simulated copy delays share real time, including scheduler delays.
    let selection = await ClipboardSelectionReader(ax: ax, clipboard: clipboard,
        pause: {
            try? await Task.sleep(nanoseconds: 15_000_000)
            clipboard.advance(to: ProcessInfo.processInfo.systemUptime)
        },
        isCancelled: { control.isCancelled }).read(request)
    if clipboard.copied && !clipboard.restored { exit(9) }
    return selection
}

private struct ClipboardAssertion: Error { let message: String }

@MainActor func runClipboardSelfTests() async -> Int32 {
    func request(pid: Int32? = 42, trigger: SelectionTrigger = .translateShortcut) -> NativeSelectionRequest {
        NativeSelectionRequest(trigger: trigger, pointer: SelectionPoint(x: -700, y: 150),
            foregroundApplication: nil, gestureBounds: nil, includeContext: true, foregroundProcessId: pid)
    }
    func read(_ ax: FixtureAXAccess, _ clipboard: FixtureClipboardAccess,
              trigger: SelectionTrigger = .translateShortcut,
              control: HelperRequestControl = HelperRequestControl()) async -> NativeSelection {
        await ClipboardSelectionReader(ax: ax, clipboard: clipboard, now: { clipboard.clock },
            pause: { clipboard.advance(); await Task.yield() }, isCancelled: { control.isCancelled }).read(request(trigger: trigger))
    }
    func expect(_ condition: Bool, _ message: String) throws {
        if !condition { throw ClipboardAssertion(message: message) }
    }
    func test(_ name: String, _ body: () async throws -> Void) async throws {
        try await body()
        print("PASS: " + name)
    }
    do {
        try await test("Copy success restores every original item and format") {
            let ax = FixtureAXAccess(); let clipboard = FixtureClipboardAccess()
            let original = clipboard.items
            let result = await read(ax, clipboard)
            try expect(result.text == "copied selection" && result.source == "clipboardFallback", "copied text")
            try expect(clipboard.items == original && clipboard.restored && clipboard.posts == 1, "all original formats")
            try expect(result.context == nil && result.bounds == nil, "no clipboard context or invented bounds")
        }
        try await test("An originally empty pasteboard is restored to empty") {
            let clipboard = FixtureClipboardAccess(); clipboard.items = []; clipboard.text = nil
            let result = await read(FixtureAXAccess(), clipboard)
            try expect(result.failure == "none" && clipboard.items.isEmpty && clipboard.restored, "empty restore")
        }
        try await test("Snapshot failure never sends Cmd+C") {
            let clipboard = FixtureClipboardAccess(); clipboard.snapshotError = true
            let result = await read(FixtureAXAccess(), clipboard)
            try expect(result.failure == "clipboardUnavailable" && clipboard.posts == 0 && clipboard.text == "original", "unsafe snapshot")
        }
        try await test("Promised, concealed and excessive snapshots cannot be copied over") {
            let snapshots = [
                [[PasteboardEntry(type: "com.apple.filepromise", data: Data())]],
                [[PasteboardEntry(type: "org.nspasteboard.ConcealedType", data: Data())]],
                [Array(repeating: PasteboardEntry(type: "duplicate", data: Data()), count: 65)],
                [[PasteboardEntry(type: "test.large", data: Data(count: 16 * 1_024 * 1_024 + 1))]]
            ]
            for items in snapshots {
                let clipboard = FixtureClipboardAccess(); clipboard.items = items
                let result = await read(FixtureAXAccess(), clipboard)
                try expect(result.failure == "clipboardUnavailable" && clipboard.posts == 0, "snapshot limits")
            }
        }
        try await test("A pasteboard changed during backup is preserved") {
            let clipboard = FixtureClipboardAccess(); clipboard.onSnapshot = { clipboard.userCopy() }
            let result = await read(FixtureAXAccess(), clipboard)
            try expect(result.failure == "cancelled" && clipboard.posts == 0 && clipboard.text == "user copied", "snapshot race")
        }
        try await test("Denied event posting and pressed modifiers block injection") {
            let clipboard = FixtureClipboardAccess(); clipboard.permitted = false
            try expect(await read(FixtureAXAccess(), clipboard).failure == "permissionDenied", "event permission")
            clipboard.permitted = true; clipboard.modifiers = true
            try expect(await read(FixtureAXAccess(), clipboard).failure == "protectedContent" && clipboard.posts == 0, "modifiers")
        }
        try await test("Terminal and unsafe focused controls are never copied") {
            let ax = FixtureAXAccess(); let clipboard = FixtureClipboardAccess()
            ax.target = SelectionTarget(processId: 42, bundleIdentifier: "com.apple.Terminal")
            try expect(await read(ax, clipboard).failure == "protectedContent", "terminal")
            ax.target = SelectionTarget(processId: 42, bundleIdentifier: "test.editor"); ax.elements[1]?.copyEligible = false
            try expect(await read(ax, clipboard).failure == "protectedContent" && clipboard.posts == 0, "unsafe focus")
        }
        try await test("Hit-tested selectable text enables fallback when focus is a container") {
            let ax = FixtureAXAccess(); let clipboard = FixtureClipboardAccess()
            ax.elements[1]?.copyEligible = false
            ax.hit = 2
            ax.elements[2] = FixtureElement()
            let result = await read(ax, clipboard, trigger: .mouseGesture)
            try expect(result.text == "copied selection" && clipboard.posts == 1, "hit candidate")
        }
        try await test("Protected ancestors stop before any clipboard write") {
            let ax = FixtureAXAccess(); let clipboard = FixtureClipboardAccess()
            ax.elements[1]?.parent = 2; ax.elements[2] = FixtureElement(protected: true)
            let result = await read(ax, clipboard)
            try expect(result.failure == "protectedContent" && clipboard.posts == 0, "protected ancestor")
        }
        try await test("Source and focus changes during backup never receive Cmd+C") {
            for changeFocus in [false, true] {
                let ax = FixtureAXAccess(); let clipboard = FixtureClipboardAccess()
                clipboard.onSnapshot = {
                    if changeFocus { ax.focused = 2 }
                    else { ax.target = SelectionTarget(processId: 43, bundleIdentifier: "test.editor") }
                }
                let result = await read(ax, clipboard)
                try expect(result.failure == "cancelled" && clipboard.posts == 0, "source changed")
            }
        }
        try await test("No pasteboard transition times out without changing original contents") {
            let clipboard = FixtureClipboardAccess(); clipboard.neverCopies = true
            let result = await read(FixtureAXAccess(), clipboard)
            try expect(result.failure == "timeout" && clipboard.restores == 0 && clipboard.text == "original", "no copy")
        }
        try await test("Nontext and blank copies restore the previous clipboard") {
            let values: [String?] = [nil, " "]
            for text in values {
                let clipboard = FixtureClipboardAccess(); clipboard.copiedText = text
                let result = await read(FixtureAXAccess(), clipboard)
                try expect(result.failure == "clipboardUnavailable" && clipboard.restored, "no text")
            }
        }
        try await test("Text limits restore the clipboard without returning partial text") {
            let clipboard = FixtureClipboardAccess(); clipboard.copiedText = String(repeating: "\u{1F600}", count: 10_001)
            let result = await read(FixtureAXAccess(), clipboard)
            try expect(result.failure == "cancelled" && result.text == nil && clipboard.restored, "UTF-16 limit")
        }
        try await test("Cancellation after injection waits for the in-flight copy and restores it") {
            let clipboard = FixtureClipboardAccess(); let control = HelperRequestControl()
            clipboard.copyDelay = 0.15; clipboard.onAdvance = { control.cancel() }
            let result = await read(FixtureAXAccess(), clipboard, control: control)
            try expect(result.failure == "cancelled" && clipboard.copied && clipboard.restored, "cancel cleanup")
        }
        try await test("Cancellation before injection leaves the pasteboard untouched") {
            let clipboard = FixtureClipboardAccess(); let control = HelperRequestControl(); control.cancel()
            let result = await read(FixtureAXAccess(), clipboard, control: control)
            try expect(result.failure == "cancelled" && clipboard.posts == 0, "cancel before copy")
        }
        try await test("A later user copy wins over restoration and translation") {
            let clipboard = FixtureClipboardAccess()
            clipboard.onAdvance = { if clipboard.clock >= 0.06 && clipboard.sequence == 41 { clipboard.userCopy() } }
            let result = await read(FixtureAXAccess(), clipboard)
            try expect(result.failure == "cancelled" && result.text == nil && clipboard.text == "user copied" && clipboard.restores == 0, "later copy")
        }
        try await test("User input before clipboard attribution stops reading") {
            let clipboard = FixtureClipboardAccess(); clipboard.onAdvance = { clipboard.keyDown = 1 }
            let result = await read(FixtureAXAccess(), clipboard)
            try expect(result.failure == "cancelled" && clipboard.observations == 0 && clipboard.restores == 0, "new input")
        }
        try await test("Focus and frontmost changes discard results but restore an attributed copy") {
            for changeFocus in [false, true] {
                let ax = FixtureAXAccess(); let clipboard = FixtureClipboardAccess()
                clipboard.onAdvance = {
                    if clipboard.clock >= 0.06 {
                        if changeFocus { ax.focused = 2 }
                        else { ax.target = SelectionTarget(processId: 43, bundleIdentifier: "test.editor") }
                    }
                }
                let result = await read(ax, clipboard)
                try expect(result.failure == "cancelled" && result.text == nil && clipboard.restored, "source after copy")
            }
        }
        try await test("Permission revoked during copying is typed and still cleans up") {
            let ax = FixtureAXAccess(); let clipboard = FixtureClipboardAccess()
            clipboard.onAdvance = { if clipboard.clock >= 0.06 { ax.trusted = false } }
            let result = await read(ax, clipboard)
            try expect(result.failure == "permissionDenied" && clipboard.restored, "permission revoked")
        }
        try await test("Restore failure suppresses the selected text") {
            let clipboard = FixtureClipboardAccess(); clipboard.restoreError = true
            let result = await read(FixtureAXAccess(), clipboard)
            try expect(result.diagnosticCode == "clipboard-restore-failed" && result.text == nil, "restore failure")
        }
        try await test("The final restoration check preserves a newer copy") {
            let clipboard = FixtureClipboardAccess(); clipboard.supersedeOnRestore = true
            let result = await read(FixtureAXAccess(), clipboard)
            try expect(result.failure == "cancelled" && clipboard.text == "user copied" && !clipboard.restored, "final race")
        }
        try await test("Ambiguous multiple transitions cannot be attributed or restored") {
            let clipboard = FixtureClipboardAccess(); clipboard.copyIncrements = 2
            let result = await read(FixtureAXAccess(), clipboard)
            try expect(result.failure == "cancelled" && result.text == nil && clipboard.restores == 0, "ambiguous copy")
        }
        try await test("Failed event creation preserves the original pasteboard") {
            let clipboard = FixtureClipboardAccess(); clipboard.postError = true
            let result = await read(FixtureAXAccess(), clipboard)
            try expect(result.failure == "permissionDenied" && clipboard.posts == 0 && clipboard.text == "original", "event failure")
        }
        print("Clipboard transaction self-test passed. Fake pasteboard and events only; no desktop data or authorization access.")
        return 0
    } catch let error as ClipboardAssertion {
        fputs("Clipboard self-test failed: " + error.message + "\n", stderr)
    } catch { fputs("Clipboard self-test failed\n", stderr) }
    return 1
}
