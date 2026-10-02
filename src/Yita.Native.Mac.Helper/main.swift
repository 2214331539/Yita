import AppKit
import ApplicationServices
import Darwin
import Foundation

private let protocolVersion = 1
private let bundleIdentifier = "com.yita.desktop.native-helper"
private let maximumRequestBytes = 64_000
private let arguments = CommandLine.arguments
private let selfTest = arguments.contains("--self-test")
private let parentPID: Int32? = {
    guard let index = arguments.firstIndex(of: "--parent-pid"), index + 1 < arguments.count else { return nil }
    return Int32(arguments[index + 1])
}()

private struct Request: Decodable {
    let version: Int
    let id: String
    let command: String
}

private struct Permissions: Encodable {
    let accessibility: Bool
    let inputMonitoring: Bool
}

private struct Capabilities: Encodable {
    let selection = false
    let clipboardFallback = false
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
}

private func emit(_ response: Response) {
    guard var data = try? JSONEncoder().encode(response) else { exit(2) }
    data.append(0x0A)
    do { try FileHandle.standardOutput.write(contentsOf: data) }
    catch { exit(0) }
}

private func permissions() -> Permissions {
    if selfTest { return Permissions(accessibility: false, inputMonitoring: false) }
    return Permissions(accessibility: AXIsProcessTrusted(), inputMonitoring: CGPreflightListenEventAccess())
}

private func handle(_ data: Data) {
    guard let request = try? JSONDecoder().decode(Request.self, from: data),
          request.version == protocolVersion, request.id.count == 32,
          request.id.allSatisfy({ $0.isHexDigit }) else { exit(2) }
    var response = Response(id: request.id, status: "ok")
    switch request.command {
    case "permissions":
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
        emit(Response(id: request.id, status: "error", diagnosticCode: "selection-not-implemented")); return
    default:
        emit(Response(id: request.id, status: "error", diagnosticCode: "unsupported-command")); return
    }
    emit(response)
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
        if count == 0 { exit(pending.isEmpty ? 0 : 2) }
        if count < 0 {
            if errno == EINTR { continue }
            exit(0)
        }
        for byte in buffer.prefix(count) {
            if byte == 0x0A {
                let request = pending
                pending.removeAll(keepingCapacity: true)
                DispatchQueue.main.sync { handle(request) }
            } else {
                guard pending.count < maximumRequestBytes else { exit(2) }
                pending.append(byte)
            }
        }
    }
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
    watchdog.setEventHandler { if getppid() != parent { exit(0) } }
    watchdog.resume()
}
DispatchQueue.global(qos: .utility).async { readRequests() }
if selfTest { dispatchMain() }
else { NSApplication.shared.run() }
