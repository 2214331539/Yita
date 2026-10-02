import AppKit
import ApplicationServices
import Carbon
import Foundation

struct NativeInputOptions: Decodable {
    let mouseEnabled: Bool
    let sequence: Int64?
}

struct NativeInputEvent: Encodable {
    let kind: String
    let sequence: Int64
    let pointer: SelectionPoint
    let ageMilliseconds: Double
    let foregroundProcessId: Int32?
    let foregroundApplication: String?
    let modified: Bool
}

struct NativeInputSnapshot: Encodable {
    let mouseRunning: Bool
    let hotkeyRunning: Bool
    let sequence: Int64
    let events: [NativeInputEvent]
}

private struct QueuedInput {
    let kind: String
    let sequence: Int64
    let point: SelectionPoint
    let timestamp: TimeInterval
    let target: SelectionTarget?
    let modified: Bool
}

final class NativeInputQueue {
    private let lock = NSLock()
    private var items: [QueuedInput] = []
    private var generation: Int64 = 0
    private var target: SelectionTarget?
    private let ownerPID: Int32?
    init(ownerPID: Int32?) { self.ownerPID = ownerPID }
    var sequence: Int64 { lock.lock(); defer { lock.unlock() }; return generation }

    func setTarget(_ value: SelectionTarget?, now: TimeInterval) {
        lock.lock(); defer { lock.unlock() }
        if target == value { return }
        target = value
        append("cancel", point: SelectionPoint(x: 0, y: 0), timestamp: now, modified: false)
    }

    func record(_ kind: String, point: SelectionPoint, timestamp: TimeInterval, modified: Bool = false) {
        lock.lock(); defer { lock.unlock() }
        guard point.isValid, abs(point.x) <= 10_000_000, abs(point.y) <= 10_000_000, timestamp.isFinite else { return }
        if kind != "translateClipboard", target?.processId == ownerPID || target?.processId == getpid() {
            append("cancel", point: point, timestamp: timestamp, modified: false)
            return
        }
        let actual = (kind == "pointerDown" || kind == "pointerUp") && target == nil ? "cancel" : kind
        append(actual, point: point, timestamp: timestamp, modified: modified)
    }

    private func append(_ kind: String, point: SelectionPoint, timestamp: TimeInterval, modified: Bool) {
        guard generation < Int64.max else { items.removeAll(); return }
        generation += 1
        let overflow = items.count >= 64
        if overflow { items.removeAll(keepingCapacity: true) }
        items.append(QueuedInput(kind: overflow ? "cancel" : kind, sequence: generation, point: point,
            timestamp: timestamp, target: target, modified: modified))
    }

    func drain(now: TimeInterval) -> (sequence: Int64, events: [NativeInputEvent]) {
        lock.lock(); defer { lock.unlock() }
        var result: [NativeInputEvent] = []
        for item in items {
            let age = (now - item.timestamp) * 1_000
            let stale = !age.isFinite || age < 0 || age > 500
            if stale { result.removeAll(keepingCapacity: true) }
            result.append(NativeInputEvent(kind: stale ? "cancel" : item.kind, sequence: item.sequence, pointer: item.point,
                ageMilliseconds: stale ? 0 : age, foregroundProcessId: item.target?.processId,
                foregroundApplication: item.target?.bundleIdentifier, modified: item.modified))
        }
        items.removeAll(keepingCapacity: true)
        return (generation, result)
    }
}

private let mouseCallback: CGEventTapCallBack = { _, type, event, context in
    guard let context = context else { return Unmanaged.passUnretained(event) }
    let monitor = Unmanaged<NativeMouseMonitor>.fromOpaque(context).takeUnretainedValue()
    monitor.receive(type, event: event)
    return Unmanaged.passUnretained(event)
}

// This dedicated run loop continues to invalidate old selections during synchronous AX calls.
final class NativeMouseMonitor {
    private let queue: NativeInputQueue
    private let condition = NSCondition()
    private var tap: CFMachPort?
    private var runLoop: CFRunLoop?
    private var ready = false
    private var stopping = false
    private var running = false
    init(queue: NativeInputQueue) { self.queue = queue }
    var isRunning: Bool { condition.lock(); defer { condition.unlock() }; return running }

    func start() -> Bool {
        Thread { self.run() }.start()
        condition.lock(); defer { condition.unlock() }
        let deadline = Date().addingTimeInterval(1)
        while !ready { if !condition.wait(until: deadline) { return false } }
        return running
    }

    private func run() {
        let types: [CGEventType] = [.leftMouseDown, .leftMouseUp, .rightMouseDown, .otherMouseDown, .keyDown]
        let mask = types.reduce(CGEventMask(0)) { $0 | (CGEventMask(1) << $1.rawValue) }
        let port = CGEvent.tapCreate(tap: .cgSessionEventTap, place: .headInsertEventTap, options: .listenOnly,
            eventsOfInterest: mask, callback: mouseCallback, userInfo: Unmanaged.passUnretained(self).toOpaque())
        let source = port.flatMap { CFMachPortCreateRunLoopSource(kCFAllocatorDefault, $0, 0) }
        condition.lock()
        guard let port = port, let source = source, !stopping else {
            ready = true; condition.broadcast(); condition.unlock()
            if let port = port { CFMachPortInvalidate(port) }
            return
        }
        tap = port; runLoop = CFRunLoopGetCurrent(); running = true; ready = true
        CFRunLoopAddSource(runLoop, source, .commonModes)
        CGEvent.tapEnable(tap: port, enable: true)
        condition.broadcast(); condition.unlock()
        CFRunLoopRun()
        CGEvent.tapEnable(tap: port, enable: false)
        CFMachPortInvalidate(port)
        condition.lock(); running = false; tap = nil; runLoop = nil; condition.unlock()
    }

    func receive(_ type: CGEventType, event: CGEvent) {
        let now = ProcessInfo.processInfo.systemUptime
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
            condition.lock(); running = false; condition.unlock()
            queue.record("cancel", point: SelectionPoint(x: 0, y: 0), timestamp: now)
            return
        }
        guard event.getIntegerValueField(.eventSourceUserData) != SystemClipboardSelectionAccess.injectedEventMarker else { return }
        let age = now - Double(event.timestamp) / 1_000_000_000
        if !age.isFinite || age < -0.05 || age > 0.5 {
            queue.record("cancel", point: SelectionPoint(x: 0, y: 0), timestamp: now)
            return
        }
        let point = SelectionPoint(x: event.location.x, y: event.location.y)
        let kind = type == .leftMouseDown || type == .rightMouseDown || type == .otherMouseDown ? "pointerDown"
            : type == .leftMouseUp ? "pointerUp" : "cancel"
        let modified = type == .rightMouseDown || type == .otherMouseDown
            || !event.flags.intersection([.maskCommand, .maskControl, .maskShift, .maskAlternate]).isEmpty
        queue.record(kind, point: point, timestamp: now, modified: modified)
    }

    func stop() {
        condition.lock(); stopping = true; running = false
        let port = tap; let loop = runLoop; condition.unlock()
        if let port = port { CGEvent.tapEnable(tap: port, enable: false) }
        if let loop = loop { CFRunLoopStop(loop); CFRunLoopWakeUp(loop) }
    }
    deinit { stop() }
}

private let hotkeyCallback: EventHandlerUPP = { _, event, context in
    guard let event = event, let context = context else { return OSStatus(eventNotHandledErr) }
    var identifier = EventHotKeyID()
    guard GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
        nil, MemoryLayout<EventHotKeyID>.size, nil, &identifier) == noErr,
        identifier.signature == 0x59495441, identifier.id == 1 else { return OSStatus(eventNotHandledErr) }
    let input = Unmanaged<NativeInputManager>.fromOpaque(context).takeUnretainedValue()
    input.receiveHotkey(pressed: GetEventKind(event) == UInt32(kEventHotKeyPressed))
    return noErr
}

final class NativeInputManager {
    let queue: NativeInputQueue
    private let selfTest: Bool
    private let fixture: String?
    private let ownerProcessId: Int32?
    private var monitor: NativeMouseMonitor?
    private var hotkey: EventHotKeyRef?
    private var handler: EventHandlerRef?
    private var observer: NSObjectProtocol?
    private var wanted = false
    private var configured = false
    private var lastRefresh: TimeInterval = -10
    private var tapStarts: [TimeInterval] = []
    private var fixtureEmitted = false
    private var hotkeyHeld = false
    var diagnostic: String?
    var sequence: Int64 { queue.sequence }
    var pointer: SelectionPoint {
        if selfTest { return SelectionPoint(x: -700, y: 150) }
        let location = CGEvent(source: nil)?.location ?? .zero
        return SelectionPoint(x: location.x, y: location.y)
    }

    init(ownerPID: Int32?, selfTest: Bool, fixture: String?) {
        queue = NativeInputQueue(ownerPID: ownerPID)
        ownerProcessId = ownerPID
        self.selfTest = selfTest; self.fixture = fixture
    }

    func configure(mouseEnabled: Bool) {
        wanted = mouseEnabled; configured = true; lastRefresh = -10
        if selfTest {
            if fixture != nil { queue.setTarget(SelectionTarget(processId: 42, bundleIdentifier: "test.editor"), now: ProcessInfo.processInfo.systemUptime) }
            return
        }
        if observer == nil {
            updateTarget()
            observer = NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didActivateApplicationNotification,
                object: nil, queue: .main) { [weak self] _ in self?.updateTarget() }
        }
        if hotkey == nil {
            let types = [EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed)),
                         EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyReleased))]
            if handler == nil {
                _ = types.withUnsafeBufferPointer { buffer in
                    InstallEventHandler(GetApplicationEventTarget(), hotkeyCallback, buffer.count, buffer.baseAddress,
                        Unmanaged.passUnretained(self).toOpaque(), &handler)
                }
            }
            if handler != nil {
                let identifier = EventHotKeyID(signature: 0x59495441, id: 1)
                if RegisterEventHotKey(UInt32(kVK_ANSI_T), UInt32(cmdKey | shiftKey), identifier,
                    GetApplicationEventTarget(), 0, &hotkey) != noErr { diagnostic = "hotkey-conflict" }
            }
        }
        refresh()
    }

    func receiveHotkey(pressed: Bool) {
        if !pressed { hotkeyHeld = false; return }
        guard !hotkeyHeld else { return }
        hotkeyHeld = true
        queue.record("translateClipboard", point: pointer, timestamp: ProcessInfo.processInfo.systemUptime)
    }

    private func updateTarget() {
        let app = NSWorkspace.shared.frontmostApplication
        let target = app.map { SelectionTarget(processId: $0.processIdentifier, bundleIdentifier: $0.bundleIdentifier) }
        queue.setTarget(target, now: ProcessInfo.processInfo.systemUptime)
    }

    private func refresh() {
        guard !selfTest, configured else { return }
        let now = ProcessInfo.processInfo.systemUptime
        guard now - lastRefresh >= 1 else { return }
        lastRefresh = now
        updateTarget()
        guard wanted, CGPreflightListenEventAccess() else {
            if monitor != nil { monitor?.stop(); monitor = nil; queue.record("cancel", point: pointer, timestamp: now) }
            if wanted { diagnostic = "input-monitoring-denied" }
            return
        }
        if monitor?.isRunning == true { return }
        tapStarts.removeAll { now - $0 >= 30 }
        guard tapStarts.count < 3 else { diagnostic = "input-tap-unavailable"; return }
        tapStarts.append(now)
        monitor?.stop()
        let next = NativeMouseMonitor(queue: queue)
        monitor = next
        if !next.start() { next.stop(); monitor = nil; diagnostic = "input-tap-unavailable" }
        else { diagnostic = hotkey == nil ? "hotkey-conflict" : nil }
    }

    func poll() -> NativeInputSnapshot {
        if !configured { diagnostic = "input-not-configured" }
        else if diagnostic == "input-not-configured" { diagnostic = nil }
        refresh()
        if selfTest, fixture == "drag", configured, wanted, !fixtureEmitted {
            fixtureEmitted = true
            let now = ProcessInfo.processInfo.systemUptime
            queue.record("pointerDown", point: SelectionPoint(x: -800, y: 120), timestamp: now)
            queue.record("pointerUp", point: SelectionPoint(x: -700, y: 150), timestamp: now)
        }
        let batch = queue.drain(now: ProcessInfo.processInfo.systemUptime)
        // Filter nonactivating Yita windows outside the low-level callback as well as in Desktop.
        let windows = !selfTest && !batch.events.isEmpty
            ? CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] : nil
        let events = batch.events.map { item -> NativeInputEvent in
            guard item.kind == "pointerDown" || item.kind == "pointerUp", let windows = windows else { return item }
            for window in windows.prefix(512) {
                guard let bounds = window[kCGWindowBounds as String] as? [String: Any],
                      let rectangle = CGRect(dictionaryRepresentation: bounds as CFDictionary),
                      let pid = window[kCGWindowOwnerPID as String] as? NSNumber,
                      (window[kCGWindowAlpha as String] as? NSNumber)?.doubleValue != 0,
                      rectangle.contains(CGPoint(x: item.pointer.x, y: item.pointer.y)) else { continue }
                if pid.int32Value == getpid() || pid.int32Value == ownerProcessId {
                    return NativeInputEvent(kind: "cancel", sequence: item.sequence, pointer: item.pointer,
                        ageMilliseconds: item.ageMilliseconds, foregroundProcessId: item.foregroundProcessId,
                        foregroundApplication: item.foregroundApplication, modified: item.modified)
                }
                break
            }
            return item
        }
        return NativeInputSnapshot(mouseRunning: selfTest ? configured && wanted && fixture != nil : monitor?.isRunning == true,
            hotkeyRunning: selfTest ? configured && fixture != nil : hotkey != nil, sequence: batch.sequence, events: events)
    }

    func stop() {
        hotkeyHeld = false
        monitor?.stop(); monitor = nil
        if let hotkey = hotkey { UnregisterEventHotKey(hotkey); self.hotkey = nil }
        if let handler = handler { RemoveEventHandler(handler); self.handler = nil }
        if let observer = observer { NSWorkspace.shared.notificationCenter.removeObserver(observer); self.observer = nil }
    }
    deinit { stop() }
}
