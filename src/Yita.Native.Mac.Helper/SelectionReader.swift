import AppKit
import ApplicationServices
import Foundation

struct SelectionPoint: Codable {
    let x: Double
    let y: Double
    var isValid: Bool { x.isFinite && y.isFinite && Float(x).isFinite && Float(y).isFinite }
}

struct SelectionRectangle: Codable {
    let x: Double
    let y: Double
    let width: Double
    let height: Double
    var isValid: Bool {
        x.isFinite && y.isFinite && width.isFinite && height.isFinite && width > 0 && height > 0
            && (x + width).isFinite && (y + height).isFinite
    }
}

enum SelectionTrigger: String, Decodable {
    case mouseGesture, translateShortcut, trayCommand
}

struct NativeSelectionRequest: Decodable {
    let trigger: SelectionTrigger
    let pointer: SelectionPoint
    let foregroundApplication: String?
    let gestureBounds: SelectionRectangle?
    let includeContext: Bool
    let foregroundProcessId: Int32?

    var isValid: Bool {
        pointer.isValid && (foregroundProcessId == nil || foregroundProcessId! > 1)
            && (gestureBounds == nil || (gestureBounds!.x.isFinite && gestureBounds!.y.isFinite
                && gestureBounds!.width.isFinite && gestureBounds!.height.isFinite
                && gestureBounds!.width >= 0 && gestureBounds!.height >= 0))
    }
}

struct NativeSelection: Encodable {
    var text: String?
    var source = "accessibility"
    var bounds: SelectionRectangle?
    var failure = "none"
    var diagnosticCode: String?
    var context: String?

    static func failed(_ error: SelectionReadError) -> NativeSelection {
        NativeSelection(failure: error.failure, diagnosticCode: error.code)
    }
}

enum SelectionReadError: Error {
    case permissionDenied, noTarget, targetChanged, protectedContent, timeout, unsupported, textLimit, invalidRequest, unavailable, empty, cancelled

    var failure: String {
        switch self {
        case .permissionDenied: return "permissionDenied"
        case .noTarget, .unsupported: return "unsupportedApplication"
        case .targetChanged, .textLimit, .cancelled: return "cancelled"
        case .protectedContent: return "protectedContent"
        case .timeout: return "timeout"
        case .invalidRequest, .unavailable: return "unknown"
        case .empty: return "empty"
        }
    }

    var code: String {
        switch self {
        case .permissionDenied: return "ax-permission-denied"
        case .noTarget: return "ax-target-unavailable"
        case .targetChanged: return "ax-target-changed"
        case .protectedContent: return "ax-protected-content"
        case .timeout: return "ax-timeout"
        case .unsupported: return "ax-unsupported"
        case .textLimit: return "ax-text-limit"
        case .invalidRequest: return "invalid-selection-request"
        case .unavailable: return "ax-unavailable"
        case .empty: return "ax-empty"
        case .cancelled: return "selection-cancelled"
        }
    }
}

struct SelectionTarget: Equatable {
    let processId: Int32
    let bundleIdentifier: String?
}

protocol AXSelectionAccess {
    associatedtype Element
    func isTrusted() -> Bool
    func frontmostTarget() -> SelectionTarget?
    func isOwnTarget(_ target: SelectionTarget) -> Bool
    func prepare(target: SelectionTarget, deadline: TimeInterval) throws
    func focusedElement() throws -> Element?
    func element(at point: SelectionPoint) throws -> Element?
    func parent(of element: Element) throws -> Element?
    func sameElement(_ left: Element, _ right: Element) -> Bool
    func isProtected(_ element: Element) throws -> Bool
    func selectedText(_ element: Element) throws -> String?
    func selectedRange(_ element: Element) throws -> CFRange?
    func text(_ element: Element, range: CFRange) throws -> String?
    func bounds(_ element: Element, range: CFRange) throws -> SelectionRectangle?
    func characterCount(_ element: Element) throws -> Int?
    func allowsCopy(_ element: Element) throws -> Bool
}

func inspectSelectionPaths<Access: AXSelectionAccess>(_ access: Access, origins: [Access.Element],
    check: () throws -> Void) throws -> [Access.Element] {
    var candidates: [Access.Element] = []
    for origin in origins {
        var current: Access.Element? = origin
        var path: [Access.Element] = []
        for _ in 0..<16 {
            guard let element = current else { break }
            guard !path.contains(where: { access.sameElement($0, element) }) else { throw SelectionReadError.unsupported }
            try check()
            path.append(element)
            if try access.isProtected(element) { throw SelectionReadError.protectedContent }
            if !candidates.contains(where: { access.sameElement($0, element) }) { candidates.append(element) }
            current = try access.parent(of: element)
        }
        if current != nil { throw SelectionReadError.unsupported }
    }
    return candidates
}

// The same selection policy runs against AX and deterministic CI fixtures.
final class AXSelectionReader<Access: AXSelectionAccess> {
    private let access: Access
    private let now: () -> TimeInterval
    private let budget: TimeInterval
    private let isCancelled: () -> Bool
    private let maximumTextLength = 20_000

    init(access: Access, budget: TimeInterval = 1.2,
         now: @escaping () -> TimeInterval = { ProcessInfo.processInfo.systemUptime },
         isCancelled: @escaping () -> Bool = { false }) {
        self.access = access
        self.budget = budget
        self.now = now
        self.isCancelled = isCancelled
    }

    func read(_ request: NativeSelectionRequest) -> NativeSelection {
        do { return try readChecked(request) }
        catch let error as SelectionReadError { return .failed(error) }
        catch { return .failed(.unavailable) }
    }

    private func readChecked(_ request: NativeSelectionRequest) throws -> NativeSelection {
        guard request.isValid else { throw SelectionReadError.invalidRequest }
        guard access.isTrusted() else { throw SelectionReadError.permissionDenied }
        guard let target = access.frontmostTarget(), !access.isOwnTarget(target) else { throw SelectionReadError.noTarget }
        guard request.foregroundProcessId == nil || request.foregroundProcessId == target.processId,
              request.foregroundApplication == nil || request.foregroundApplication == target.bundleIdentifier else {
            throw SelectionReadError.targetChanged
        }
        let deadline = now() + budget
        func checkSource() throws {
            guard !isCancelled() else { throw SelectionReadError.cancelled }
            guard access.isTrusted() else { throw SelectionReadError.permissionDenied }
            guard access.frontmostTarget() == target else { throw SelectionReadError.targetChanged }
        }
        func checkTarget() throws {
            try checkSource()
            guard now() < deadline else { throw SelectionReadError.timeout }
        }
        // Range, bounds and context are optional once text has been acquired within the budget.
        // A reader may expose selected text while rejecting these additional AX queries.
        func supplemental<Value>(_ read: () throws -> Value?) throws -> Value? {
            do { try checkTarget(); return try read() }
            catch let error as SelectionReadError {
                switch error {
                case .timeout, .unsupported, .unavailable: return nil
                default: throw error
                }
            }
        }
        try access.prepare(target: target, deadline: deadline)
        try checkTarget()
        let focused = try access.focusedElement()
        let hit = request.trigger == .mouseGesture ? try access.element(at: request.pointer) : nil
        // Inspect both paths for protected ancestors before reading any text.
        let candidates = try inspectSelectionPaths(access, origins: [focused, hit].compactMap({ $0 }), check: checkTarget)
        guard !candidates.isEmpty else { throw SelectionReadError.unsupported }
        var foundSelectionAttribute = false
        for element in candidates {
            try checkTarget()
            let direct = try access.selectedText(element)
            try checkTarget()
            let hasDirectText = direct?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty == false
            let range: CFRange?
            if hasDirectText { range = try supplemental { try access.selectedRange(element) } }
            else { range = try access.selectedRange(element) }
            if direct != nil || range != nil { foundSelectionAttribute = true }
            var selected = direct
            let validRange = range.flatMap { valid($0) ? $0 : nil }
            if !hasDirectText {
                if let range = validRange { selected = try access.text(element, range: range) }
                try checkTarget()
            }
            guard let text = selected, !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { continue }
            guard text.utf16.count <= maximumTextLength else { throw SelectionReadError.textLimit }
            var result = NativeSelection(text: text)
            if let range = validRange {
                // AX bounds are Quartz global points, with a top-left origin.
                let rectangle = try supplemental { try access.bounds(element, range: range) }
                result.bounds = rectangle?.isValid == true ? rectangle : nil
                if request.includeContext, range.length <= 1_400 {
                    result.context = try supplemental {
                        let start = max(0, range.location - 300)
                        let end = range.location + range.length
                        let count = try access.characterCount(element)
                        try checkTarget()
                        let contextEnd = count.map { max(end, min($0, end > Int.max - 300 ? end : end + 300)) } ?? end
                        guard contextEnd - start <= 2_000,
                              let context = try access.text(element, range: CFRange(location: start, length: contextEnd - start)),
                              context.utf16.count <= 2_000 else { return nil }
                        return context
                    }
                }
            }
            // Supplemental timeouts must not discard good text, but stale/unauthorized text is still rejected.
            try checkSource()
            return result
        }
        try checkTarget()
        throw foundSelectionAttribute ? SelectionReadError.empty : SelectionReadError.unsupported
    }

    private func valid(_ range: CFRange) -> Bool {
        range.location >= 0 && range.length > 0 && range.length <= maximumTextLength
            && range.location <= Int.max - range.length
    }
}

final class SystemAXSelectionAccess: AXSelectionAccess {
    typealias Element = AXUIElement
    private let ownerPID: Int32?
    private var application: AXUIElement?
    private var targetPID: Int32 = 0
    private var deadline: TimeInterval = 0

    init(ownerPID: Int32?) { self.ownerPID = ownerPID }
    func isTrusted() -> Bool { AXIsProcessTrusted() }
    func frontmostTarget() -> SelectionTarget? {
        guard let app = NSWorkspace.shared.frontmostApplication else { return nil }
        return SelectionTarget(processId: app.processIdentifier, bundleIdentifier: app.bundleIdentifier)
    }
    func isOwnTarget(_ target: SelectionTarget) -> Bool {
        target.processId == getpid() || target.processId == ownerPID || target.bundleIdentifier == "com.yita.desktop"
    }
    func prepare(target: SelectionTarget, deadline: TimeInterval) throws {
        targetPID = target.processId
        self.deadline = deadline
        application = AXUIElementCreateApplication(targetPID)
    }
    func focusedElement() throws -> AXUIElement? {
        guard let app = application else { throw SelectionReadError.noTarget }
        return try asElement(attribute(app, kAXFocusedUIElementAttribute as CFString))
    }
    func element(at point: SelectionPoint) throws -> AXUIElement? {
        guard let app = application else { throw SelectionReadError.noTarget }
        try configure(app)
        var element: AXUIElement?
        let error = AXUIElementCopyElementAtPosition(app, Float(point.x), Float(point.y), &element)
        return try accept(error) ? element : nil
    }
    func parent(of element: AXUIElement) throws -> AXUIElement? {
        try asElement(attribute(element, kAXParentAttribute as CFString))
    }
    func sameElement(_ left: AXUIElement, _ right: AXUIElement) -> Bool { CFEqual(left, right) }
    func isProtected(_ element: AXUIElement) throws -> Bool {
        let role = try attribute(element, kAXRoleAttribute as CFString) as? String
        let subrole = try attribute(element, kAXSubroleAttribute as CFString) as? String
        return role == "AXSecureTextField" || subrole == (kAXSecureTextFieldSubrole as String)
    }
    func selectedText(_ element: AXUIElement) throws -> String? {
        try attribute(element, kAXSelectedTextAttribute as CFString) as? String
    }
    func selectedRange(_ element: AXUIElement) throws -> CFRange? {
        guard let value = try attribute(element, kAXSelectedTextRangeAttribute as CFString),
              CFGetTypeID(value) == AXValueGetTypeID() else { return nil }
        let axValue = unsafeBitCast(value, to: AXValue.self)
        guard AXValueGetType(axValue) == .cfRange else { return nil }
        var range = CFRange()
        return AXValueGetValue(axValue, .cfRange, &range) ? range : nil
    }
    func text(_ element: AXUIElement, range: CFRange) throws -> String? {
        var range = range
        guard let value = AXValueCreate(.cfRange, &range) else { return nil }
        return try parameter(element, kAXStringForRangeParameterizedAttribute as CFString, value) as? String
    }
    func bounds(_ element: AXUIElement, range: CFRange) throws -> SelectionRectangle? {
        var range = range
        guard let value = AXValueCreate(.cfRange, &range),
              let result = try parameter(element, kAXBoundsForRangeParameterizedAttribute as CFString, value),
              CFGetTypeID(result) == AXValueGetTypeID() else { return nil }
        let axValue = unsafeBitCast(result, to: AXValue.self)
        guard AXValueGetType(axValue) == .cgRect else { return nil }
        var rect = CGRect.zero
        guard AXValueGetValue(axValue, .cgRect, &rect) else { return nil }
        return SelectionRectangle(x: rect.origin.x, y: rect.origin.y, width: rect.width, height: rect.height)
    }
    func characterCount(_ element: AXUIElement) throws -> Int? {
        let value = try attribute(element, kAXNumberOfCharactersAttribute as CFString) as? NSNumber
        guard let count = value?.intValue, count >= 0 else { return nil }
        return count
    }
    func allowsCopy(_ element: AXUIElement) throws -> Bool {
        let role = try attribute(element, kAXRoleAttribute as CFString) as? String
        if ["AXTextField", "AXTextArea", "AXStaticText", "AXWebArea", "AXDocument", "AXPDFView"].contains(role ?? "") { return true }
        // Many native editors and document viewers expose the focused text as
        // a child of a generic container. The clipboard reader has already
        // walked the focused/hit-test paths and rejected protected ancestors,
        // so these structural roles are safe compatibility candidates here.
        if ["AXGroup", "AXWindow", "AXScrollArea", "AXUnknown", "AXOutline", "AXList",
            "AXTable", "AXRow", "AXCell", "AXColumn", "AXTextView", "AXParagraph",
            "AXHeading", "AXLink", "AXCode"].contains(role ?? "") { return true }
        let bundle = frontmostTarget()?.bundleIdentifier ?? ""
        return ["com.apple.Preview", "com.adobe.Reader", "com.adobe.Acrobat.Pro", "net.sourceforge.skim-app.skim"].contains(bundle)
            && ["AXScrollArea", "AXGroup", "AXUnknown", "AXWindow"].contains(role ?? "")
    }

    private func asElement(_ value: CFTypeRef?) -> AXUIElement? {
        guard let value = value, CFGetTypeID(value) == AXUIElementGetTypeID() else { return nil }
        return unsafeBitCast(value, to: AXUIElement.self)
    }
    private func configure(_ element: AXUIElement) throws {
        let remaining = deadline - ProcessInfo.processInfo.systemUptime
        guard remaining > 0 else { throw SelectionReadError.timeout }
        var pid: pid_t = 0
        guard AXUIElementGetPid(element, &pid) == .success, pid == targetPID else { throw SelectionReadError.targetChanged }
        guard AXUIElementSetMessagingTimeout(element, Float(min(0.15, remaining))) == .success else {
            throw SelectionReadError.unavailable
        }
    }
    private func attribute(_ element: AXUIElement, _ name: CFString) throws -> CFTypeRef? {
        try configure(element)
        var value: CFTypeRef?
        let error = AXUIElementCopyAttributeValue(element, name, &value)
        return try accept(error) ? value : nil
    }
    private func parameter(_ element: AXUIElement, _ name: CFString, _ parameter: CFTypeRef) throws -> CFTypeRef? {
        try configure(element)
        var value: CFTypeRef?
        let error = AXUIElementCopyParameterizedAttributeValue(element, name, parameter, &value)
        return try accept(error) ? value : nil
    }
    private func accept(_ error: AXError) throws -> Bool {
        switch error {
        case .success: return true
        case .attributeUnsupported, .parameterizedAttributeUnsupported, .noValue, .notImplemented: return false
        case .apiDisabled: throw SelectionReadError.permissionDenied
        case .cannotComplete: throw SelectionReadError.timeout
        case .invalidUIElement: throw SelectionReadError.noTarget
        default: throw SelectionReadError.unavailable
        }
    }
}
