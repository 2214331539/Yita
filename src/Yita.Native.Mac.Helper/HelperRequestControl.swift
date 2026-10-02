import Foundation

final class HelperRequestControl {
    private let lock = NSLock()
    private var cancelled = false
    var isCancelled: Bool {
        lock.lock(); defer { lock.unlock() }
        return cancelled
    }
    func cancel() {
        lock.lock(); defer { lock.unlock() }
        cancelled = true
    }
}

final class HelperRequestRegistry {
    private let lock = NSLock()
    private var requests: [String: HelperRequestControl] = [:]
    private var closing: Int32?

    func register(_ id: String) -> HelperRequestControl? {
        lock.lock(); defer { lock.unlock() }
        guard closing == nil, requests[id] == nil, requests.count < 4 else { return nil }
        let control = HelperRequestControl()
        requests[id] = control
        return control
    }
    func cancel(_ id: String) {
        lock.lock(); defer { lock.unlock() }
        requests[id]?.cancel()
    }
    func finish(_ id: String) -> Int32? {
        lock.lock(); defer { lock.unlock() }
        requests.removeValue(forKey: id)
        return requests.isEmpty ? closing : nil
    }
    func shutdown(_ code: Int32) {
        lock.lock(); defer { lock.unlock() }
        closing = closing ?? code
        for control in requests.values { control.cancel() }
        if requests.isEmpty { DispatchQueue.main.async { exit(code) } }
    }
}
