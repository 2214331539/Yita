import AppKit
import ApplicationServices
import Darwin
import Foundation

private let protocolVersion = 2
private let bundleIdentifier = "com.yita.desktop.native-helper"
private let maximumRequestBytes = 64_000
private let arguments = CommandLine.arguments
private let requests = HelperRequestRegistry()
private let selfTest = arguments.contains("--self-test")
private let selectionFixture: String? = {
    guard selfTest, let index = arguments.firstIndex(of: "--selection-fixture"), index + 1 < arguments.count else { return nil }
    return arguments[index + 1]
}()
private let parentPID: Int32? = {
    guard let index = arguments.firstIndex(of: "--parent-pid"), index + 1 < arguments.count else { return nil }
    return Int32(arguments[index + 1])
}()

private struct Request: Decodable {
    let version: Int
    let id: String
    let command: String
    let selection: NativeSelectionRequest?
    let allowClipboardFallback: Bool?
}

private struct Permissions: Encodable {
    let accessibility: Bool
    let inputMonitoring: Bool
    let eventPosting: Bool
}

private struct Capabilities: Encodable {
    let selection = true
    let clipboardFallback = true
    let globalInput = false
}

private struct Response: Encodable {
    let version = protocolVersion
    let id: String
    let status: String
    var bundleIdentifier: String?
    var processId: Int32?
    var permissions: Permissions?
    var capabilities: Capabilities?
    var diagnosticCode: String?
    var selection: NativeSelection?
}

private func emit(_ response: Response) {
    guard var data = try? JSONEncoder().encode(response) else { exit(2) }
    data.append(0x0A)
    do { try FileHandle.standardOutput.write(contentsOf: data) }
    catch { exit(0) }
}

private func permissions() -> Permissions {
    if selfTest { return Permissions(accessibility: false, inputMonitoring: false, eventPosting: false) }
    return Permissions(accessibility: AXIsProcessTrusted(), inputMonitoring: CGPreflightListenEventAccess(), eventPosting: CGPreflightPostEventAccess())
}

@MainActor private func handle(_ request: Request, control: HelperRequestControl) async {
    var response = Response(id: request.id, status: "ok")
    switch request.command {
    case "permissions":
        response.processId = getpid()
        response.permissions = permissions()
        response.capabilities = Capabilities()
    case "requestAccessibility":
        if selfTest { emit(Response(id: request.id, status: "error", diagnosticCode: "self-test-action-disabled")); return }
        let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
        _ = AXIsProcessTrustedWithOptions(options)
        response.permissions = permissions()
        response.capabilities = Capabilities()
    case "openAccessibilitySettings":
        if selfTest { emit(Response(id: request.id, status: "error", diagnosticCode: "self-test-action-disabled")); return }
        guard let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility"),
              NSWorkspace.shared.open(url) else {
            emit(Response(id: request.id, status: "error", diagnosticCode: "open-settings-failed")); return
        }
    case "readSelection":
        guard let selection = request.selection else {
            response.selection = .failed(.invalidRequest)
            emit(response); return
        }
        if selfTest, let fixture = selectionFixture, fixture.hasPrefix("clipboard") {
            response.selection = await readClipboardFixture(fixture, request: selection,
                allowClipboardFallback: request.allowClipboardFallback == true, control: control)
        }
        else if selfTest, let fixture = selectionFixture { response.selection = readSelectionFixture(fixture, request: selection) }
        else if selfTest { response.selection = .failed(.permissionDenied) }
        else { response.selection = await readNativeSelection(selection, allowClipboardFallback: request.allowClipboardFallback == true,
            ownerPID: parentPID, control: control) }
    default:
        emit(Response(id: request.id, status: "error", diagnosticCode: "unsupported-command")); return
    }
    emit(response)
}

private func enqueue(_ data: Data) -> Bool {
    guard let request = try? JSONDecoder().decode(Request.self, from: data),
          request.version == protocolVersion, request.id.count == 32,
          request.id.utf8.allSatisfy({ (48...57).contains($0) || (97...102).contains($0) }) else {
        requests.shutdown(2); return false
    }
    if request.command == "cancelSelection" { requests.cancel(request.id); return true }
    guard let control = requests.register(request.id) else { requests.shutdown(2); return false }
    DispatchQueue.main.async {
        Task { @MainActor in
            await handle(request, control: control)
            if let code = requests.finish(request.id) { exit(code) }
        }
    }
    return true
}

// Only the pipe reader runs off the Cocoa loop; permission APIs and actions run on the main thread.
private func readRequests() {
    var pending = Data()
    var buffer = [UInt8](repeating: 0, count: 4096)
    while true {
        // A pipe read must return available bytes without waiting to fill the buffer.
        let count = buffer.withUnsafeMutableBytes { bytes in
            Darwin.read(STDIN_FILENO, bytes.baseAddress, bytes.count)
        }
        if count == 0 { requests.shutdown(pending.isEmpty ? 0 : 2); return }
        if count < 0 {
            if errno == EINTR { continue }
            requests.shutdown(0); return
        }
        for byte in buffer.prefix(count) {
            if byte == 0x0A {
                let request = pending
                pending.removeAll(keepingCapacity: true)
                if !enqueue(request) { return }
            } else {
                guard pending.count < maximumRequestBytes else { requests.shutdown(2); return }
                pending.append(byte)
            }
        }
    }
}

if arguments.contains("--selection-self-test") {
    exit(runSelectionSelfTests())
}
if arguments.contains("--clipboard-self-test") {
    Task { @MainActor in exit(await runClipboardSelfTests()) }
    dispatchMain()
}
if !selfTest {
    _ = NSApplication.shared
    NSApplication.shared.setActivationPolicy(.prohibited)
}
emit(Response(id: "ready", status: "ready", bundleIdentifier: bundleIdentifier, processId: getpid()))
let watchdog = DispatchSource.makeTimerSource(queue: DispatchQueue.global(qos: .utility))
if let parent = parentPID {
    guard parent > 1, parent == getppid() else { exit(2) }
    watchdog.schedule(deadline: .now() + 1, repeating: 1)
    watchdog.setEventHandler { if getppid() != parent { requests.shutdown(0) } }
    watchdog.resume()
}
DispatchQueue.global(qos: .utility).async { readRequests() }
if selfTest { dispatchMain() }
else { NSApplication.shared.run() }
