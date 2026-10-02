import Foundation

func runInputSelfTests() -> Int32 {
    struct Assertion: Error { let message: String }
    func expect(_ condition: Bool, _ message: String) throws {
        if !condition { throw Assertion(message: message) }
    }
    func test(_ name: String, _ body: () throws -> Void) throws {
        try body(); print("PASS: " + name)
    }
    let point = SelectionPoint(x: -800, y: 120)
    let target = SelectionTarget(processId: 42, bundleIdentifier: "test.editor")
    do {
        try test("Pointer events preserve Quartz points and the captured source identity") {
            let queue = NativeInputQueue(ownerPID: 99)
            queue.setTarget(target, now: 10)
            _ = queue.drain(now: 10)
            queue.record("pointerDown", point: point, timestamp: 10)
            queue.record("pointerUp", point: SelectionPoint(x: -700, y: 150), timestamp: 10.1)
            let batch = queue.drain(now: 10.2)
            try expect(batch.events.count == 2 && batch.events[0].pointer.x == -800, "negative screen points")
            try expect(batch.events[1].foregroundProcessId == 42 && batch.events[1].foregroundApplication == "test.editor", "source identity")
            try expect(batch.events[0].sequence < batch.events[1].sequence && batch.sequence == batch.events[1].sequence, "ordered generation")
        }
        try test("Foreground changes invalidate pending selections without retaining previous targets") {
            let queue = NativeInputQueue(ownerPID: 99)
            queue.setTarget(target, now: 10)
            let previous = queue.sequence
            queue.setTarget(SelectionTarget(processId: 43, bundleIdentifier: "test.other"), now: 10.1)
            let batch = queue.drain(now: 10.2)
            try expect(batch.sequence > previous && batch.events.last?.kind == "cancel", "source cancellation")
            try expect(batch.events.last?.foregroundProcessId == 43, "new source")
        }
        try test("Own-process pointer input cannot become an external gesture") {
            let queue = NativeInputQueue(ownerPID: 99)
            queue.setTarget(SelectionTarget(processId: 99, bundleIdentifier: "com.yita.desktop"), now: 10)
            queue.record("pointerDown", point: point, timestamp: 10)
            queue.record("pointerUp", point: point, timestamp: 10)
            try expect(queue.drain(now: 10).events.allSatisfy { $0.kind == "cancel" }, "own process")
        }
        try test("Missing source identity cancels pointer input") {
            let queue = NativeInputQueue(ownerPID: 99)
            queue.record("pointerDown", point: point, timestamp: 10)
            try expect(queue.drain(now: 10).events.first?.kind == "cancel", "missing source")
        }
        try test("Queue overflow emits a reset and never leaves an unbounded partial drag") {
            let queue = NativeInputQueue(ownerPID: 99)
            queue.setTarget(target, now: 10)
            for _ in 0..<200 { queue.record("pointerDown", point: point, timestamp: 10) }
            let batch = queue.drain(now: 10)
            try expect(batch.events.count <= 64 && batch.events.first?.kind == "cancel", "bounded overflow")
            try expect(batch.events.last?.sequence == batch.sequence, "overflow generation")
        }
        try test("Stale and future-dated input resets gestures instead of reading old selections") {
            for time in [9.0, 11.0] {
                let queue = NativeInputQueue(ownerPID: 99)
                queue.setTarget(target, now: 10)
                queue.record("pointerUp", point: point, timestamp: time)
                let batch = queue.drain(now: 10)
                try expect(batch.events.last?.kind == "cancel" && batch.events.last?.ageMilliseconds == 0, "freshness")
            }
        }
        try test("Modifier flags reach the host without keyboard text") {
            let queue = NativeInputQueue(ownerPID: 99)
            queue.setTarget(target, now: 10)
            queue.record("pointerDown", point: point, timestamp: 10, modified: true)
            let batch = queue.drain(now: 10)
            try expect(batch.events.last?.modified == true && batch.events.last?.kind == "pointerDown", "modifiers")
        }
        try test("Manual clipboard commands remain available while the own app has focus") {
            let queue = NativeInputQueue(ownerPID: 99)
            queue.setTarget(SelectionTarget(processId: 99, bundleIdentifier: "com.yita.desktop"), now: 10)
            queue.record("translateClipboard", point: point, timestamp: 10)
            try expect(queue.drain(now: 10).events.last?.kind == "translateClipboard", "manual command")
        }
        try test("Invalid coordinates never enter the input queue") {
            let queue = NativeInputQueue(ownerPID: 99)
            for x in [Double.nan, Double.infinity, 10_000_001] {
                queue.record("pointerDown", point: SelectionPoint(x: x, y: 0), timestamp: 10)
            }
            try expect(queue.sequence == 0 && queue.drain(now: 10).events.isEmpty, "invalid points")
        }
        try test("Draining a batch cannot repeat a previous gesture") {
            let queue = NativeInputQueue(ownerPID: 99)
            queue.setTarget(target, now: 10)
            queue.record("pointerDown", point: point, timestamp: 10)
            let first = queue.drain(now: 10)
            let second = queue.drain(now: 10)
            try expect(!first.events.isEmpty && second.events.isEmpty && first.sequence == second.sequence, "single delivery")
        }
        try test("Holding the shortcut produces one command until release") {
            let input = NativeInputManager(ownerPID: 99, selfTest: true, fixture: nil)
            input.receiveHotkey(pressed: true)
            input.receiveHotkey(pressed: true)
            input.receiveHotkey(pressed: false)
            input.receiveHotkey(pressed: true)
            let batch = input.queue.drain(now: ProcessInfo.processInfo.systemUptime)
            try expect(batch.events.count == 2 && batch.events.allSatisfy { $0.kind == "translateClipboard" }, "shortcut repeat")
        }
        try test("Disabled synthetic mouse input still keeps the independent shortcut capability") {
            let input = NativeInputManager(ownerPID: 99, selfTest: true, fixture: "drag")
            input.configure(mouseEnabled: false)
            let batch = input.poll()
            try expect(!batch.mouseRunning && batch.hotkeyRunning && batch.events.allSatisfy { $0.kind == "cancel" }, "separate capabilities")
        }
        try test("Overlapping sleep display and user-session reasons require all reasons to clear") {
            let session = NativeDesktopSession()
            try expect(session.set(.sleep, suspended: true) && !session.isActive && session.generation == 1, "sleep")
            try expect(!session.set(.displaySleep, suspended: true), "overlapping display sleep")
            session.set(.inactiveUser, suspended: true)
            try expect(!session.set(.sleep, suspended: false) && !session.isActive, "still suspended")
            session.set(.displaySleep, suspended: false)
            try expect(session.set(.inactiveUser, suspended: false) && session.isActive && session.generation == 2, "all clear")
        }
        try test("Duplicate workspace notifications cannot repeatedly advance session generation") {
            let session = NativeDesktopSession()
            for _ in 0..<20 { session.set(.sleep, suspended: true) }
            for _ in 0..<20 { session.set(.sleep, suspended: false) }
            try expect(session.generation == 2 && session.isActive, "deduplicated lifecycle")
        }
        try test("Suspension clears queued drags and shortcuts and is persistent across empty polls") {
            let input = NativeInputManager(ownerPID: 99, selfTest: true, fixture: "drag")
            input.configure(mouseEnabled: true)
            input.receiveHotkey(pressed: true)
            input.setSession(.sleep, suspended: true)
            input.receiveHotkey(pressed: true)
            for _ in 0..<3 {
                let batch = input.poll()
                try expect(!batch.sessionActive && batch.sessionGeneration == 1 && !batch.mouseRunning
                    && !batch.hotkeyRunning && batch.events.isEmpty, "persistent suspended snapshot")
            }
            input.setSession(.sleep, suspended: false)
            let resumed = input.poll()
            try expect(resumed.sessionActive && resumed.sessionGeneration == 2 && resumed.mouseRunning && resumed.hotkeyRunning, "resumed input")
            try expect(resumed.events.allSatisfy { $0.kind != "translateClipboard" }, "old shortcut discarded")
        }
        try test("Queue reset changes the selection generation and cannot replay a previous drag") {
            let queue = NativeInputQueue(ownerPID: 99)
            queue.setTarget(target, now: 10)
            queue.record("pointerDown", point: point, timestamp: 10)
            let previous = queue.sequence
            queue.reset(now: 11)
            let batch = queue.drain(now: 11)
            try expect(batch.sequence > previous && batch.events.count == 1 && batch.events[0].kind == "cancel", "reset")
        }
        print("Native input policy self-test passed. Synthetic input only; no event taps, hotkeys, clipboard or authorization access.")
        return 0
    } catch let error as Assertion { fputs("Input self-test failed: " + error.message + "\n", stderr) }
    catch { fputs("Input self-test failed\n", stderr) }
    return 1
}
