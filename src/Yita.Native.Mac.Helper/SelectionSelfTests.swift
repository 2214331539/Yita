import Foundation

private struct FixtureElement {
    var text: String? = "hello"
    var range: CFRange? = CFRange(location: 0, length: 5)
    var parent: Int?
    var protected = false
}

private final class FixtureAXAccess: AXSelectionAccess {
    typealias Element = Int
    var trusted = true
    var ownTarget = false
    var target = SelectionTarget(processId: 42, bundleIdentifier: "test.editor")
    var focused: Int? = 1
    var hit: Int?
    var elements: [Int: FixtureElement] = [1: FixtureElement()]
    var document = "hello"
    var rectangle: SelectionRectangle? = SelectionRectangle(x: -800, y: 120, width: 90, height: 18)
    var rangeQueries: [CFRange] = []
    var textReads = 0
    var clock: TimeInterval = 0
    var readError: SelectionReadError?
    var afterText: (() -> Void)?

    func isTrusted() -> Bool { trusted }
    func frontmostTarget() -> SelectionTarget? { target }
    func isOwnTarget(_ target: SelectionTarget) -> Bool { ownTarget }
    func prepare(target: SelectionTarget, deadline: TimeInterval) throws { }
    func focusedElement() throws -> Int? { focused }
    func element(at point: SelectionPoint) throws -> Int? { hit }
    func parent(of element: Int) throws -> Int? { elements[element]?.parent }
    func sameElement(_ left: Int, _ right: Int) -> Bool { left == right }
    func isProtected(_ element: Int) throws -> Bool { elements[element]?.protected == true }
    func selectedText(_ element: Int) throws -> String? {
        textReads += 1
        if let error = readError { throw error }
        afterText?()
        return elements[element]?.text
    }
    func selectedRange(_ element: Int) throws -> CFRange? { elements[element]?.range }
    func text(_ element: Int, range: CFRange) throws -> String? {
        rangeQueries.append(range)
        let value = document as NSString
        guard range.location >= 0, range.length >= 0, range.location <= value.length,
              range.length <= value.length - range.location else { return nil }
        return value.substring(with: NSRange(location: range.location, length: range.length))
    }
    func bounds(_ element: Int, range: CFRange) throws -> SelectionRectangle? { rectangle }
    func characterCount(_ element: Int) throws -> Int? { (document as NSString).length }
}

private struct SelectionAssertion: Error { let message: String }

func readSelectionFixture(_ name: String, request: NativeSelectionRequest) -> NativeSelection {
    guard name == "range" else { return .failed(.invalidRequest) }
    let access = FixtureAXAccess()
    access.document = "prefix hello world suffix"
    access.elements[1]?.text = nil
    access.elements[1]?.range = CFRange(location: 7, length: 11)
    return AXSelectionReader(access: access, now: { access.clock }).read(request)
}

// Explicit CI mode only: fixtures never access AppKit, AX, clipboard, or user data.
func runSelectionSelfTests() -> Int32 {
    func request(context: Bool = false, pid: Int32? = nil, bundle: String? = nil,
                 trigger: SelectionTrigger = .translateShortcut) -> NativeSelectionRequest {
        NativeSelectionRequest(trigger: trigger, pointer: SelectionPoint(x: -700, y: 150),
            foregroundApplication: bundle, gestureBounds: nil, includeContext: context, foregroundProcessId: pid)
    }
    func read(_ access: FixtureAXAccess, _ request: NativeSelectionRequest) -> NativeSelection {
        AXSelectionReader(access: access, now: { access.clock }).read(request)
    }
    func expect(_ condition: Bool, _ message: String) throws {
        if !condition { throw SelectionAssertion(message: message) }
    }
    func test(_ name: String, _ body: () throws -> Void) throws {
        try body()
        print("PASS: " + name)
    }
    do {
        try test("Direct selected text preserves Quartz coordinates, without context reads") {
            let access = FixtureAXAccess()
            let selection = read(access, request(pid: 42, bundle: "test.editor"))
            try expect(selection.text == "hello" && selection.failure == "none", "direct text")
            try expect(selection.bounds?.x == -800 && selection.bounds?.height == 18, "negative display coordinates")
            try expect(selection.context == nil && access.rangeQueries.isEmpty, "context opt-in")
        }
        try test("Selection range fallback reads only the selected substring") {
            let access = FixtureAXAccess()
            access.elements[1]?.text = nil
            access.document = "before hello after"
            access.elements[1]?.range = CFRange(location: 7, length: 5)
            let selection = read(access, request())
            try expect(selection.text == "hello" && access.rangeQueries.count == 1, "range fallback")
            try expect(access.rangeQueries[0].location == 7 && access.rangeQueries[0].length == 5, "bounded query")
        }
        try test("Context uses a bounded range only after explicit opt-in") {
            let access = FixtureAXAccess()
            access.document = "before hello after"
            access.elements[1]?.range = CFRange(location: 7, length: 5)
            let selection = read(access, request(context: true))
            try expect(selection.context == access.document && access.rangeQueries.count == 1, "context query")
            try expect(access.rangeQueries[0].length <= 2_000, "context limit")
        }
        try test("Missing AX bounds preserve text and leave the pointer anchor available") {
            let access = FixtureAXAccess()
            access.rectangle = nil
            let selection = read(access, request())
            try expect(selection.text == "hello" && selection.bounds == nil, "optional bounds")
        }
        try test("Malformed bounds are discarded") {
            for rect in [SelectionRectangle(x: .infinity, y: 0, width: 10, height: 10),
                         SelectionRectangle(x: 0, y: 0, width: -1, height: 10)] {
                let access = FixtureAXAccess()
                access.rectangle = rect
                try expect(read(access, request()).bounds == nil, "invalid bounds")
            }
        }
        try test("Protected focused elements and ancestors never read text") {
            for ancestor in [false, true] {
                let access = FixtureAXAccess()
                if ancestor {
                    access.elements[1]?.parent = 2
                    access.elements[2] = FixtureElement(protected: true)
                } else { access.elements[1]?.protected = true }
                let selection = read(access, request())
                try expect(selection.failure == "protectedContent" && access.textReads == 0, "protected target")
            }
        }
        try test("Hit-test protected paths block stale focused text") {
            let access = FixtureAXAccess()
            access.hit = 2
            access.elements[2] = FixtureElement(protected: true)
            let selection = read(access, request(trigger: .mouseGesture))
            try expect(selection.failure == "protectedContent" && access.textReads == 0, "protected hit path")
        }
        try test("Permission denial and own application are distinct, without text reads") {
            let access = FixtureAXAccess()
            access.trusted = false
            try expect(read(access, request()).failure == "permissionDenied", "permission")
            access.trusted = true
            access.ownTarget = true
            try expect(read(access, request()).failure == "unsupportedApplication" && access.textReads == 0, "own process")
        }
        try test("PID and bundle mismatches reject the previous target") {
            let access = FixtureAXAccess()
            for value in [request(pid: 43), request(bundle: "other.editor")] {
                try expect(read(access, value).failure == "cancelled" && access.textReads == 0, "source mismatch")
            }
        }
        try test("Foreground changes during AX reading discard all retrieved data") {
            let access = FixtureAXAccess()
            access.afterText = { access.target = SelectionTarget(processId: 43, bundleIdentifier: "test.editor") }
            let selection = read(access, request())
            try expect(selection.failure == "cancelled" && selection.text == nil && selection.context == nil, "stale data")
        }
        try test("AX errors and total time budget become structured timeout results") {
            let access = FixtureAXAccess()
            access.readError = .timeout
            try expect(read(access, request()).failure == "timeout", "AX timeout")
            access.readError = nil
            access.afterText = { access.clock = 2 }
            try expect(read(access, request()).failure == "timeout", "total budget")
        }
        try test("Empty and unsupported selections are separate") {
            let access = FixtureAXAccess()
            access.elements[1]?.text = ""
            access.elements[1]?.range = CFRange(location: 0, length: 0)
            try expect(read(access, request()).failure == "empty", "empty selection")
            access.elements[1]?.text = nil
            access.elements[1]?.range = nil
            try expect(read(access, request()).failure == "unsupportedApplication", "unsupported selection")
        }
        try test("UTF-16 text limits never return a silently truncated selection") {
            let access = FixtureAXAccess()
            access.elements[1]?.text = String(repeating: "\u{1F9A6}", count: 10_001)
            let selection = read(access, request())
            try expect(selection.failure == "cancelled" && selection.text == nil, "UTF-16 limit")
        }
        try test("Invalid and overflowing AX ranges never issue parameterized text queries") {
            for range in [CFRange(location: -1, length: 5), CFRange(location: Int.max, length: 5),
                          CFRange(location: 0, length: 20_001)] {
                let access = FixtureAXAccess()
                access.elements[1]?.text = nil
                access.elements[1]?.range = range
                _ = read(access, request(context: true))
                try expect(access.rangeQueries.isEmpty, "invalid range")
            }
        }
        try test("Invalid input coordinates stop before any AX reads") {
            let access = FixtureAXAccess()
            let value = NativeSelectionRequest(trigger: .mouseGesture, pointer: SelectionPoint(x: .nan, y: 0),
                foregroundApplication: nil, gestureBounds: nil, includeContext: false, foregroundProcessId: nil)
            try expect(read(access, value).diagnosticCode == "invalid-selection-request" && access.textReads == 0, "invalid point")
        }
        try test("Cyclic or excessive ancestor paths stop before reading text") {
            let cyclic = FixtureAXAccess()
            cyclic.elements[1]?.parent = 1
            try expect(read(cyclic, request()).failure == "unsupportedApplication" && cyclic.textReads == 0, "cyclic path")
            let deep = FixtureAXAccess()
            for index in 1...18 { deep.elements[index] = FixtureElement(parent: index < 18 ? index + 1 : nil) }
            try expect(read(deep, request()).failure == "unsupportedApplication" && deep.textReads == 0, "deep path")
        }
        try test("Mouse hit-test provides a candidate when focused AX text is unsupported") {
            let access = FixtureAXAccess()
            access.elements[1]?.text = nil
            access.elements[1]?.range = nil
            access.hit = 2
            access.elements[2] = FixtureElement()
            try expect(read(access, request(trigger: .mouseGesture)).text == "hello", "hit selection")
        }
        print("AX selection policy self-test passed. No real Accessibility, desktop selection, clipboard or authorization access.")
        return 0
    } catch let error as SelectionAssertion {
        fputs("AX selection self-test failed: " + error.message + "\n", stderr)
    } catch { fputs("AX selection self-test failed\n", stderr) }
    return 1
}
