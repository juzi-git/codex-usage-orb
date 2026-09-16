import AppKit
import Foundation

private struct LimitWindow {
    let usedPercent: Double
    let windowMinutes: Int
    let resetsAt: TimeInterval

    var remainingPercent: Double {
        return max(0, min(100, 100 - usedPercent))
    }
}

private struct UsageSnapshot {
    let timestamp: Date
    let primary: LimitWindow?
    let secondary: LimitWindow?
    let planType: String?
    let limitID: String?
    let limitName: String?

    var scopeKey: String {
        return "\(limitID ?? "")\u{1F}\(limitName ?? "")"
    }

    var windows: [LimitWindow] {
        return [primary, secondary].compactMap { $0 }
    }

    var remainingPercent: Double {
        return windows.map(\.remainingPercent).min() ?? 0
    }

    var fiveHour: LimitWindow? {
        if let exact = windows.first(where: { (280...320).contains($0.windowMinutes) }) { return exact }
        let ordered = windows.sorted { $0.windowMinutes < $1.windowMinutes }
        if ordered.count > 1 { return ordered.first }
        guard let only = ordered.first, only.windowMinutes < 1_440 else { return nil }
        return only
    }

    var weekly: LimitWindow? {
        if let exact = windows.first(where: { (10_000...10_200).contains($0.windowMinutes) }) { return exact }
        let ordered = windows.sorted { $0.windowMinutes < $1.windowMinutes }
        if ordered.count > 1 { return ordered.last }
        guard let only = ordered.first, only.windowMinutes >= 1_440 else { return nil }
        return only
    }
}

private enum OrbStyle: String {
    case concentric
    case mainWeekArc = "main-week-arc"

    init(storedValue: String?) {
        self = storedValue == OrbStyle.mainWeekArc.rawValue ? .mainWeekArc : .concentric
    }
}

/// Reads the account rate-limit snapshot from Codex app-server and falls back
/// to rollout files. Authentication data and conversation content are never
/// retained or transmitted by the orb.
private final class CodexUsageReader {
    private let sessionsURL = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent(".codex/sessions", isDirectory: true)
    private let tailBytes: UInt64 = 1_048_576
    private let candidateFileCount = 32
    private let appServerTimeout: TimeInterval = 15
    private var appServerEverSucceeded = false

    func readLatest() -> UsageSnapshot? {
        if let snapshot = readFromAppServer() {
            appServerEverSucceeded = true
            return snapshot
        }
        if appServerEverSucceeded { return nil }

        return readFromSessionFiles()
    }

    /// Reads the same account/rateLimits endpoint used by the Codex desktop
    /// client. Rollout JSONL records are retained as a compatibility fallback,
    /// but they are snapshots from individual turns and may be stale.
    private func readFromAppServer() -> UsageSnapshot? {
        guard let executable = findCodexExecutable() else { return nil }

        let process = Process()
        let input = Pipe()
        let output = Pipe()
        process.executableURL = URL(fileURLWithPath: executable)
        process.arguments = ["app-server", "--analytics-default-enabled"]
        process.standardInput = input
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice

        do { try process.run() } catch { return nil }
        let requests = """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"clientInfo":{"name":"codex-usage-orb","version":"1.0.0"},"capabilities":{}}}
        {"jsonrpc":"2.0","method":"initialized","params":{}}
        {"jsonrpc":"2.0","id":2,"method":"account/rateLimits/read","params":{}}
        """
        input.fileHandleForWriting.write(Data(requests.utf8))

        let lock = NSLock()
        let finished = DispatchSemaphore(value: 0)
        var buffer = Data()
        var result: UsageSnapshot?
        output.fileHandleForReading.readabilityHandler = { handle in
            let data = handle.availableData
            if data.isEmpty { finished.signal(); return }
            lock.lock()
            buffer.append(data)
            while let newline = buffer.firstIndex(of: 10) {
                let lineData = buffer.subdata(in: buffer.startIndex..<newline)
                buffer.removeSubrange(buffer.startIndex...newline)
                if let line = String(data: lineData, encoding: .utf8),
                   let parsed = self.parseAppServerResponse(line) {
                    result = parsed
                    finished.signal()
                    break
                }
            }
            lock.unlock()
        }

        let waitResult = finished.wait(timeout: .now() + appServerTimeout)
        output.fileHandleForReading.readabilityHandler = nil
        if process.isRunning { process.terminate() }
        process.waitUntilExit()
        lock.lock()
        let snapshot = result
        lock.unlock()
        return waitResult == .success ? snapshot : nil
    }

    private func parseAppServerResponse(_ line: String) -> UsageSnapshot? {
        guard let data = line.data(using: .utf8),
              let object = try? JSONSerialization.jsonObject(with: data),
              let root = object as? [String: Any],
              let result = root["result"] as? [String: Any],
              let rateLimits = result["rateLimits"] as? [String: Any] else { return nil }

        let primary = parseAppServerWindow(rateLimits["primary"])
        let secondary = parseAppServerWindow(rateLimits["secondary"])
        guard primary != nil || secondary != nil else { return nil }
        return UsageSnapshot(
            timestamp: Date(),
            primary: primary,
            secondary: secondary,
            planType: rateLimits["planType"] as? String,
            limitID: rateLimits["limitId"] as? String,
            limitName: rateLimits["limitName"] as? String
        )
    }

    private func parseAppServerWindow(_ value: Any?) -> LimitWindow? {
        guard let object = value as? [String: Any],
              let used = object["usedPercent"] as? NSNumber,
              let minutes = object["windowDurationMins"] as? NSNumber,
              let reset = object["resetsAt"] as? NSNumber else { return nil }
        return LimitWindow(
            usedPercent: used.doubleValue,
            windowMinutes: minutes.intValue,
            resetsAt: reset.doubleValue
        )
    }

    private func findCodexExecutable() -> String? {
        let environment = ProcessInfo.processInfo.environment
        if let configured = environment["CODEX_CLI_PATH"], FileManager.default.isExecutableFile(atPath: configured) {
            return configured
        }

        var candidates = [
            "/usr/local/bin/codex",
            "/opt/homebrew/bin/codex",
            NSHomeDirectory() + "/.local/bin/codex",
            "/Applications/Codex.app/Contents/Resources/codex",
            "/Applications/Codex.app/Contents/MacOS/codex"
        ]
        if let path = environment["PATH"] {
            candidates.append(contentsOf: path.split(separator: ":").map { String($0) + "/codex" })
        }
        return candidates.first(where: { FileManager.default.isExecutableFile(atPath: $0) })
    }

    private func readFromSessionFiles() -> UsageSnapshot? {
        let manager = FileManager.default
        guard let enumerator = manager.enumerator(
            at: sessionsURL,
            includingPropertiesForKeys: [.contentModificationDateKey, .isRegularFileKey],
            options: [.skipsHiddenFiles]
        ) else { return nil }

        var candidates: [(url: URL, modified: Date)] = []
        for case let url as URL in enumerator where url.pathExtension == "jsonl" {
            guard let values = try? url.resourceValues(forKeys: [.contentModificationDateKey, .isRegularFileKey]),
                  values.isRegularFile == true else { continue }
            candidates.append((url, values.contentModificationDate ?? .distantPast))
        }

        let records = candidates
            .sorted { $0.modified > $1.modified }
            .prefix(candidateFileCount)
            .flatMap { parseAll(in: $0.url) }
        return selectBest(records)
    }

    private func parseAll(in url: URL) -> [UsageSnapshot] {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return [] }
        defer { try? handle.close() }

        guard let length = try? handle.seekToEnd() else { return [] }
        let start = length > tailBytes ? length - tailBytes : 0
        try? handle.seek(toOffset: start)
        guard let data = try? handle.readToEnd(),
              let text = String(data: data, encoding: .utf8) else { return [] }

        var results: [UsageSnapshot] = []
        for line in text.split(separator: "\n", omittingEmptySubsequences: true).reversed() {
            guard line.contains("\"rate_limits\"") else { continue }
            if let snapshot = parseLine(Data(line.utf8)) { results.append(snapshot) }
        }
        return results
    }

    private func parseLine(_ data: Data) -> UsageSnapshot? {
        guard let object = try? JSONSerialization.jsonObject(with: data),
              let root = object as? [String: Any],
              let timestampText = root["timestamp"] as? String,
              let timestamp = parseDate(timestampText) else { return nil }

        let payload = root["payload"] as? [String: Any]
        let limits = (payload?["rate_limits"] as? [String: Any]) ?? (root["rate_limits"] as? [String: Any])
        guard let rateLimits = limits else { return nil }

        let primary = parseWindow(rateLimits["primary"])
        let secondary = parseWindow(rateLimits["secondary"])
        guard primary != nil || secondary != nil else { return nil }
        return UsageSnapshot(
            timestamp: timestamp,
            primary: primary,
            secondary: secondary,
            planType: rateLimits["plan_type"] as? String,
            limitID: rateLimits["limit_id"] as? String,
            limitName: rateLimits["limit_name"] as? String
        )
    }

    /// Selects a scope containing the 5-hour window when available and merges the
    /// newest value for each window so split rate-limit events remain complete.
    private func selectBest(_ records: [UsageSnapshot]) -> UsageSnapshot? {
        guard !records.isEmpty else { return nil }
        let groups = Dictionary(grouping: records, by: { $0.scopeKey })
        guard let selected = groups.values.max(by: { left, right in
            let leftHasFiveHour = left.contains { snapshot in snapshot.windows.contains { (280...320).contains($0.windowMinutes) } }
            let rightHasFiveHour = right.contains { snapshot in snapshot.windows.contains { (280...320).contains($0.windowMinutes) } }
            if leftHasFiveHour != rightHasFiveHour { return !leftHasFiveHour && rightHasFiveHour }
            return (left.map(\.timestamp).max() ?? .distantPast) < (right.map(\.timestamp).max() ?? .distantPast)
        }) else { return nil }

        let latest = selected.max { $0.timestamp < $1.timestamp }!
        var latestByMinutes: [Int: (window: LimitWindow, timestamp: Date)] = [:]
        for snapshot in selected {
            for window in snapshot.windows {
                if let current = latestByMinutes[window.windowMinutes], current.timestamp >= snapshot.timestamp { continue }
                latestByMinutes[window.windowMinutes] = (window, snapshot.timestamp)
            }
        }
        let windows = latestByMinutes.values.sorted { $0.window.windowMinutes < $1.window.windowMinutes }.prefix(2).map { $0.window }
        return UsageSnapshot(
            timestamp: latest.timestamp,
            primary: windows.first,
            secondary: windows.dropFirst().first,
            planType: selected.compactMap(\.planType).first,
            limitID: latest.limitID,
            limitName: latest.limitName
        )
    }

    private func parseWindow(_ value: Any?) -> LimitWindow? {
        guard let object = value as? [String: Any],
              let used = object["used_percent"] as? NSNumber,
              let minutes = object["window_minutes"] as? NSNumber,
              let reset = object["resets_at"] as? NSNumber else { return nil }
        return LimitWindow(
            usedPercent: used.doubleValue,
            windowMinutes: minutes.intValue,
            resetsAt: reset.doubleValue
        )
    }

    private func parseDate(_ value: String) -> Date? {
        let fractional = ISO8601DateFormatter()
        fractional.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let result = fractional.date(from: value) { return result }
        return ISO8601DateFormatter().date(from: value)
    }
}

private protocol OrbViewDelegate: AnyObject {
    func orbView(_ view: OrbView, showMenuFor event: NSEvent)
}

private final class OrbView: NSView {
    weak var delegate: OrbViewDelegate?
    var snapshot: UsageSnapshot? { didSet { updateToolTip(); needsDisplay = true } }
    var accent = NSColor(calibratedRed: 0.38, green: 0.86, blue: 0.09, alpha: 1) {
        didSet { needsDisplay = true }
    }
    var weeklyAccent = NSColor(calibratedRed: 169.0 / 255.0, green: 112.0 / 255.0, blue: 1, alpha: 1) {
        didSet { needsDisplay = true }
    }
    var language = "en" {
        didSet { updateToolTip(); needsDisplay = true }
    }
    var style: OrbStyle = .concentric {
        didSet { needsDisplay = true }
    }
    var concentricRingWidth: CGFloat = 5 { didSet { needsDisplay = true } }
    var weeklyArcWidth: CGFloat = 5 { didSet { needsDisplay = true } }
    private var phase: CGFloat = 0
    private var animationTimer: Timer?

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true
        animationTimer = Timer.scheduledTimer(withTimeInterval: 1.0 / 24.0, repeats: true) { [weak self] _ in
            guard let self = self else { return }
            self.phase += 0.11
            self.needsDisplay = true
        }
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    deinit { animationTimer?.invalidate() }

    override func draw(_ dirtyRect: NSRect) {
        super.draw(dirtyRect)
        guard bounds.width > 1, bounds.height > 1 else { return }

        let inset = min(bounds.width, bounds.height) * 0.055
        let circleRect = bounds.insetBy(dx: inset, dy: inset)
        let circle = NSBezierPath(ovalIn: circleRect)
        let darkTop = accent.scaled(by: 0.24)
        let darkBottom = accent.scaled(by: 0.14)
        NSGradient(starting: darkBottom, ending: darkTop)?.draw(in: circle, angle: 90)

        let fiveHour = snapshot?.fiveHour
        let weekly = snapshot?.weekly
        let warning = NSColor(calibratedRed: 1.0, green: 0.48, blue: 0.27, alpha: 1)
        let fiveColor = (fiveHour?.remainingPercent ?? 100) <= 15 ? warning : accent
        let weeklyColor = (weekly?.remainingPercent ?? 100) <= 15 ? warning : weeklyAccent

        if style == .concentric {
            let scale = circleRect.width / 150
            let outerWidth = max(1, min(14, concentricRingWidth) * scale)
            let innerWidth = max(1, outerWidth * 0.8)
            let outerInset = circleRect.width * 0.02
            let innerInset = outerInset + outerWidth / 2 + innerWidth / 2 + 5.5 * scale
            drawFullMeter(in: circleRect, inset: outerInset, percent: fiveHour?.remainingPercent,
                          color: fiveColor, lineWidth: outerWidth)
            drawFullMeter(in: circleRect, inset: innerInset, percent: weekly?.remainingPercent,
                          color: weeklyColor, lineWidth: innerWidth)
        } else {
            if let remaining = fiveHour?.remainingPercent {
                NSGraphicsContext.saveGraphicsState()
                circle.addClip()
                let fill = CGFloat(remaining) / 100
                drawWave(in: circleRect, fill: fill, phase: phase + 1.8, amplitude: 0.033, wavelength: 0.20,
                         top: accent.withAlphaComponent(0.73), bottom: accent.scaled(by: 0.55).withAlphaComponent(0.82))
                drawWave(in: circleRect, fill: fill, phase: phase, amplitude: 0.043, wavelength: 0.25,
                         top: accent, bottom: accent.scaled(by: 0.42))
                NSGraphicsContext.restoreGraphicsState()
            }
            drawPartialMeter(in: circleRect, percent: weekly?.remainingPercent, color: weeklyColor,
                             lineWidth: max(1, min(14, weeklyArcWidth) * circleRect.width / 150))
        }

        drawLabels(
            in: circleRect,
            weeklyColor: weekly == nil ? NSColor.white.withAlphaComponent(0.45) : weeklyColor
        )
    }

    private func drawFullMeter(in rect: NSRect, inset: CGFloat, percent: Double?, color: NSColor, lineWidth: CGFloat) {
        let meterRect = rect.insetBy(dx: inset + lineWidth / 2, dy: inset + lineWidth / 2)
        let track = NSBezierPath(ovalIn: meterRect)
        track.lineWidth = lineWidth
        NSColor.white.withAlphaComponent(0.18).setStroke()
        track.stroke()
        guard let percent = percent, percent > 0 else { return }
        if percent >= 99.999 {
            color.setStroke()
            track.stroke()
            return
        }
        let radius = meterRect.width / 2
        let progress = NSBezierPath()
        progress.appendArc(withCenter: NSPoint(x: meterRect.midX, y: meterRect.midY), radius: radius,
                           startAngle: 90, endAngle: 90 - CGFloat(360 * percent / 100), clockwise: true)
        progress.lineWidth = lineWidth
        progress.lineCapStyle = .round
        color.setStroke()
        progress.stroke()
    }

    private func drawPartialMeter(in rect: NSRect, percent: Double?, color: NSColor, lineWidth: CGFloat) {
        let radius = rect.width * 0.39
        let center = NSPoint(x: rect.midX, y: rect.midY)
        let track = NSBezierPath()
        track.appendArc(withCenter: center, radius: radius, startAngle: 210, endAngle: 330, clockwise: false)
        track.lineWidth = lineWidth
        track.lineCapStyle = .round
        NSColor.white.withAlphaComponent(0.18).setStroke()
        track.stroke()
        guard let percent = percent, percent > 0 else { return }
        let progress = NSBezierPath()
        progress.appendArc(withCenter: center, radius: radius, startAngle: 210,
                           endAngle: 210 + CGFloat(120 * percent / 100), clockwise: false)
        progress.lineWidth = lineWidth
        progress.lineCapStyle = .round
        color.setStroke()
        progress.stroke()
    }

    private func drawWave(in rect: NSRect, fill: CGFloat, phase: CGFloat, amplitude: CGFloat,
                          wavelength: CGFloat, top: NSColor, bottom: NSColor) {
        let baseline = rect.minY + rect.height * max(0.04, min(0.96, fill))
        let path = NSBezierPath()
        path.move(to: NSPoint(x: rect.minX, y: rect.minY))
        let steps = max(28, Int(rect.width / 2))
        for index in 0...steps {
            let fraction = CGFloat(index) / CGFloat(steps)
            let x = rect.minX + rect.width * fraction
            let y = baseline + sin((fraction / wavelength) + phase) * rect.height * amplitude
            path.line(to: NSPoint(x: x, y: y))
        }
        path.line(to: NSPoint(x: rect.maxX, y: rect.minY))
        path.close()
        NSGradient(starting: bottom, ending: top)?.draw(in: path, angle: 90)
    }

    private func drawLabels(in rect: NSRect, weeklyColor: NSColor) {
        let fiveHour = snapshot?.fiveHour
        let weekly = snapshot?.weekly
        let percent = fiveHour.map { "\(Int($0.remainingPercent.rounded()))%" } ?? "--"
        let subtitle = fiveHour == nil
            ? localized(language, "5-hour unavailable", "5小时暂无数据")
            : localized(language, "5-hour remaining", "5小时剩余")
        let weeklyText = localized(language, "W ", "周 ") + (weekly.map { "\(Int($0.remainingPercent.rounded()))%" } ?? "--")

        let percentScale: CGFloat = style == .concentric ? 0.23 : 0.27
        let percentFont = NSFont.systemFont(ofSize: rect.width * percentScale, weight: .semibold)
        let percentAttributes: [NSAttributedString.Key: Any] = [
            .font: percentFont,
            .foregroundColor: NSColor.white
        ]
        let percentSize = percent.size(withAttributes: percentAttributes)
        percent.draw(
            at: NSPoint(x: rect.midX - percentSize.width / 2, y: rect.midY - percentSize.height * 0.28),
            withAttributes: percentAttributes
        )

        guard rect.width >= 80 else { return }
        let subtitleFont = NSFont.systemFont(ofSize: max(7, rect.width * 0.065), weight: .medium)
        let subtitleAttributes: [NSAttributedString.Key: Any] = [
            .font: subtitleFont,
            .foregroundColor: accent.mixed(with: .white, weight: 0.84)
        ]
        let subtitleSize = subtitle.size(withAttributes: subtitleAttributes)
        subtitle.draw(
            at: NSPoint(x: rect.midX - subtitleSize.width / 2, y: rect.midY - percentSize.height * 0.68),
            withAttributes: subtitleAttributes
        )

        let weeklyFont = NSFont.systemFont(ofSize: max(7, rect.width * 0.062), weight: .semibold)
        let weeklyAttributes: [NSAttributedString.Key: Any] = [
            .font: weeklyFont,
            .foregroundColor: weeklyColor
        ]
        let weeklySize = weeklyText.size(withAttributes: weeklyAttributes)
        let bottomOffset = style == .concentric ? rect.height * 0.18 : rect.height * 0.13
        weeklyText.draw(
            at: NSPoint(x: rect.midX - weeklySize.width / 2, y: rect.minY + bottomOffset),
            withAttributes: weeklyAttributes
        )
    }

    override func mouseDown(with event: NSEvent) {
        window?.performDrag(with: event)
    }

    override func rightMouseDown(with event: NSEvent) {
        delegate?.orbView(self, showMenuFor: event)
    }

    private func updateToolTip() {
        guard let snapshot = snapshot else {
            toolTip = localized(language,
                "No Codex usage data was found. Complete at least one Codex conversation first.",
                "尚未找到 Codex 用量数据，请先完成一次 Codex 对话。")
            return
        }
        var lines = [localized(language, "Codex remaining usage", "Codex 剩余用量")]
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: language == "zh" ? "zh_CN" : "en_US_POSIX")
        formatter.dateFormat = language == "zh" ? "M月d日 HH:mm" : "MMM d, HH:mm"
        for item in snapshot.windows.sorted(by: { $0.windowMinutes < $1.windowMinutes }) {
            let reset = formatter.string(from: Date(timeIntervalSince1970: item.resetsAt))
            if language == "zh" {
                lines.append("\(windowName(item.windowMinutes, language))：\(Int(item.remainingPercent.rounded()))%（\(reset) 重置）")
            } else {
                lines.append("\(windowName(item.windowMinutes, language)): \(Int(item.remainingPercent.rounded()))% (resets \(reset))")
            }
        }
        if let plan = snapshot.planType { lines.append(localized(language, "Plan: \(plan)", "方案：\(plan)")) }
        toolTip = lines.joined(separator: "\n")
    }
}

private final class AppearanceControls: NSObject {
    let view: NSView
    let slider: NSSlider
    let sizeLabel: NSTextField
    let concentricRingWidthSlider: NSSlider
    let concentricRingWidthLabel: NSTextField
    let weeklyArcWidthSlider: NSSlider
    let weeklyArcWidthLabel: NSTextField
    let fiveHourColorWell: NSColorWell
    let weeklyColorWell: NSColorWell
    let languagePopup: NSPopUpButton
    let stylePopup: NSPopUpButton
    let resetButton: NSButton
    var onChange: ((CGFloat, CGFloat, CGFloat, NSColor, NSColor, String, OrbStyle) -> Void)?

    init(size: CGFloat, concentricRingWidth: CGFloat, weeklyArcWidth: CGFloat, color: NSColor, weeklyColor: NSColor, language: String, style: OrbStyle) {
        let container = NSStackView()
        container.orientation = .vertical
        container.alignment = .leading
        container.spacing = 12
        container.translatesAutoresizingMaskIntoConstraints = false

        let languageRow = NSStackView()
        languageRow.orientation = .horizontal
        languageRow.spacing = 8
        let languageTitle = NSTextField(labelWithString: localized(language, "Language", "语言"))
        languageTitle.setContentHuggingPriority(.required, for: .horizontal)
        languagePopup = NSPopUpButton(frame: .zero, pullsDown: false)
        languagePopup.addItems(withTitles: ["English", "中文"])
        languagePopup.selectItem(at: language == "zh" ? 1 : 0)
        languageRow.addArrangedSubview(languageTitle)
        languageRow.addArrangedSubview(languagePopup)

        let styleRow = NSStackView()
        styleRow.orientation = .horizontal
        styleRow.spacing = 8
        let styleTitle = NSTextField(labelWithString: localized(language, "Display style", "显示样式"))
        styleTitle.setContentHuggingPriority(.required, for: .horizontal)
        stylePopup = NSPopUpButton(frame: .zero, pullsDown: false)
        stylePopup.addItems(withTitles: language == "zh"
            ? ["同心双环", "主值 + 周弧"]
            : ["Concentric rings", "Main value + weekly arc"])
        stylePopup.selectItem(at: style == .mainWeekArc ? 1 : 0)
        styleRow.addArrangedSubview(styleTitle)
        styleRow.addArrangedSubview(stylePopup)

        let sizeRow = NSStackView()
        sizeRow.orientation = .horizontal
        sizeRow.spacing = 8
        let sizeTitle = NSTextField(labelWithString: localized(language, "Size", "尺寸"))
        sizeTitle.setContentHuggingPriority(.required, for: .horizontal)
        slider = NSSlider(value: Double(size), minValue: 50, maxValue: 300, target: nil, action: nil)
        slider.numberOfTickMarks = 26
        slider.allowsTickMarkValuesOnly = true
        sizeLabel = NSTextField(labelWithString: "\(Int(size.rounded())) px")
        sizeLabel.alignment = .right
        sizeLabel.frame.size.width = 58
        sizeRow.addArrangedSubview(sizeTitle)
        sizeRow.addArrangedSubview(slider)
        sizeRow.addArrangedSubview(sizeLabel)

        let concentricWidthRow = NSStackView()
        concentricWidthRow.orientation = .horizontal
        concentricWidthRow.spacing = 8
        let concentricWidthTitle = NSTextField(labelWithString: localized(language, "Ring width", "同心环宽"))
        concentricWidthTitle.setContentHuggingPriority(.required, for: .horizontal)
        concentricRingWidthSlider = NSSlider(value: Double(concentricRingWidth), minValue: 2, maxValue: 14, target: nil, action: nil)
        concentricRingWidthSlider.numberOfTickMarks = 13
        concentricRingWidthSlider.allowsTickMarkValuesOnly = true
        concentricRingWidthLabel = NSTextField(labelWithString: "\(Int(concentricRingWidth.rounded())) px")
        concentricRingWidthLabel.alignment = .right
        concentricWidthRow.addArrangedSubview(concentricWidthTitle)
        concentricWidthRow.addArrangedSubview(concentricRingWidthSlider)
        concentricWidthRow.addArrangedSubview(concentricRingWidthLabel)

        let weeklyArcWidthRow = NSStackView()
        weeklyArcWidthRow.orientation = .horizontal
        weeklyArcWidthRow.spacing = 8
        let weeklyArcWidthTitle = NSTextField(labelWithString: localized(language, "Arc width", "周弧宽度"))
        weeklyArcWidthTitle.setContentHuggingPriority(.required, for: .horizontal)
        weeklyArcWidthSlider = NSSlider(value: Double(weeklyArcWidth), minValue: 2, maxValue: 14, target: nil, action: nil)
        weeklyArcWidthSlider.numberOfTickMarks = 13
        weeklyArcWidthSlider.allowsTickMarkValuesOnly = true
        weeklyArcWidthLabel = NSTextField(labelWithString: "\(Int(weeklyArcWidth.rounded())) px")
        weeklyArcWidthLabel.alignment = .right
        weeklyArcWidthRow.addArrangedSubview(weeklyArcWidthTitle)
        weeklyArcWidthRow.addArrangedSubview(weeklyArcWidthSlider)
        weeklyArcWidthRow.addArrangedSubview(weeklyArcWidthLabel)

        let presetNames = language == "zh"
            ? [("绿色", "#61DC18"), ("蓝色", "#37BEFF"), ("紫色", "#A970FF"), ("橙色", "#FF9F35")]
            : [("Green", "#61DC18"), ("Blue", "#37BEFF"), ("Purple", "#A970FF"), ("Orange", "#FF9F35")]

        let fiveHourColorTitle = NSTextField(labelWithString: localized(language, "5-hour color", "5小时颜色"))
        let fiveHourColorRow = NSStackView()
        fiveHourColorRow.orientation = .horizontal
        fiveHourColorRow.spacing = 6
        for preset in presetNames {
            let button = NSButton(title: preset.0, target: nil, action: nil)
            button.identifier = NSUserInterfaceItemIdentifier("five:\(preset.1)")
            button.bezelStyle = .rounded
            fiveHourColorRow.addArrangedSubview(button)
        }
        fiveHourColorWell = NSColorWell(frame: NSRect(x: 0, y: 0, width: 48, height: 28))
        fiveHourColorWell.color = color
        fiveHourColorRow.addArrangedSubview(fiveHourColorWell)

        let weeklyColorTitle = NSTextField(labelWithString: localized(language, "Weekly color", "每周颜色"))
        let weeklyColorRow = NSStackView()
        weeklyColorRow.orientation = .horizontal
        weeklyColorRow.spacing = 6
        for preset in presetNames {
            let button = NSButton(title: preset.0, target: nil, action: nil)
            button.identifier = NSUserInterfaceItemIdentifier("weekly:\(preset.1)")
            button.bezelStyle = .rounded
            weeklyColorRow.addArrangedSubview(button)
        }
        weeklyColorWell = NSColorWell(frame: NSRect(x: 0, y: 0, width: 48, height: 28))
        weeklyColorWell.color = weeklyColor
        weeklyColorRow.addArrangedSubview(weeklyColorWell)

        resetButton = NSButton(title: localized(language, "Reset", "恢复默认"), target: nil, action: nil)
        resetButton.bezelStyle = .rounded

        container.addArrangedSubview(languageRow)
        container.addArrangedSubview(styleRow)
        container.addArrangedSubview(sizeRow)
        container.addArrangedSubview(concentricWidthRow)
        container.addArrangedSubview(weeklyArcWidthRow)
        container.addArrangedSubview(fiveHourColorTitle)
        container.addArrangedSubview(fiveHourColorRow)
        container.addArrangedSubview(weeklyColorTitle)
        container.addArrangedSubview(weeklyColorRow)
        container.addArrangedSubview(resetButton)
        view = container
        super.init()

        slider.target = self
        slider.action = #selector(valueChanged)
        concentricRingWidthSlider.target = self
        concentricRingWidthSlider.action = #selector(valueChanged)
        weeklyArcWidthSlider.target = self
        weeklyArcWidthSlider.action = #selector(valueChanged)
        fiveHourColorWell.target = self
        fiveHourColorWell.action = #selector(valueChanged)
        weeklyColorWell.target = self
        weeklyColorWell.action = #selector(valueChanged)
        languagePopup.target = self
        languagePopup.action = #selector(valueChanged)
        stylePopup.target = self
        stylePopup.action = #selector(valueChanged)
        resetButton.target = self
        resetButton.action = #selector(resetDefaults)
        for case let button as NSButton in fiveHourColorRow.arrangedSubviews + weeklyColorRow.arrangedSubviews {
            button.target = self
            button.action = #selector(presetClicked(_:))
        }

        NSLayoutConstraint.activate([
            container.widthAnchor.constraint(equalToConstant: 320),
            slider.widthAnchor.constraint(equalToConstant: 190),
            concentricRingWidthSlider.widthAnchor.constraint(equalToConstant: 190),
            weeklyArcWidthSlider.widthAnchor.constraint(equalToConstant: 190)
        ])
    }

    @objc private func valueChanged() {
        sizeLabel.stringValue = "\(Int(slider.doubleValue.rounded())) px"
        concentricRingWidthLabel.stringValue = "\(Int(concentricRingWidthSlider.doubleValue.rounded())) px"
        weeklyArcWidthLabel.stringValue = "\(Int(weeklyArcWidthSlider.doubleValue.rounded())) px"
        onChange?(
            CGFloat(slider.doubleValue),
            CGFloat(concentricRingWidthSlider.doubleValue),
            CGFloat(weeklyArcWidthSlider.doubleValue),
            fiveHourColorWell.color,
            weeklyColorWell.color,
            languagePopup.indexOfSelectedItem == 1 ? "zh" : "en",
            stylePopup.indexOfSelectedItem == 1 ? .mainWeekArc : .concentric
        )
    }

    @objc private func presetClicked(_ sender: NSButton) {
        guard let identifier = sender.identifier?.rawValue,
              let separator = identifier.firstIndex(of: ":"),
              let color = NSColor(hex: String(identifier[identifier.index(after: separator)...])) else { return }
        if identifier.hasPrefix("weekly:") { weeklyColorWell.color = color }
        else { fiveHourColorWell.color = color }
        valueChanged()
    }

    @objc private func resetDefaults() {
        slider.doubleValue = 168
        sizeLabel.stringValue = "168 px"
        concentricRingWidthSlider.doubleValue = 5
        weeklyArcWidthSlider.doubleValue = 5
        fiveHourColorWell.color = NSColor(hex: "#61DC18")!
        weeklyColorWell.color = NSColor(hex: "#A970FF")!
        languagePopup.selectItem(at: 0)
        stylePopup.selectItem(at: 0)
        valueChanged()
    }
}

private final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate, OrbViewDelegate {
    private let reader = CodexUsageReader()
    private let defaults = UserDefaults.standard
    private var panel: NSPanel!
    private var orbView: OrbView!
    private var refreshTimer: Timer?
    private var size: CGFloat = 168
    private var accent = NSColor(hex: "#61DC18")!
    private var weeklyAccent = NSColor(hex: "#A970FF")!
    private var language = "en"
    private var style: OrbStyle = .concentric
    private var concentricRingWidth: CGFloat = 5
    private var weeklyArcWidth: CGFloat = 5

    func applicationDidFinishLaunching(_ notification: Notification) {
        let storedSize = defaults.double(forKey: "orbSize")
        size = storedSize == 0 ? 168 : CGFloat(max(50, min(300, storedSize)))
        if let hex = defaults.string(forKey: "accent"), let color = NSColor(hex: hex) { accent = color }
        if let hex = defaults.string(forKey: "weeklyAccent"), let color = NSColor(hex: hex) { weeklyAccent = color }
        language = defaults.string(forKey: "language") == "zh" ? "zh" : "en"
        style = OrbStyle(storedValue: defaults.string(forKey: "orbStyle"))
        let storedRingWidth = defaults.double(forKey: "concentricRingWidth")
        concentricRingWidth = storedRingWidth == 0 ? 5 : CGFloat(max(2, min(14, storedRingWidth)))
        let storedArcWidth = defaults.double(forKey: "weeklyArcWidth")
        weeklyArcWidth = storedArcWidth == 0 ? 5 : CGFloat(max(2, min(14, storedArcWidth)))

        panel = NSPanel(
            contentRect: NSRect(x: 0, y: 0, width: size, height: size),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.level = .floating
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.hidesOnDeactivate = false
        panel.isMovableByWindowBackground = true
        panel.delegate = self

        orbView = OrbView(frame: NSRect(x: 0, y: 0, width: size, height: size))
        orbView.autoresizingMask = [.width, .height]
        orbView.delegate = self
        orbView.accent = accent
        orbView.weeklyAccent = weeklyAccent
        orbView.language = language
        orbView.style = style
        orbView.concentricRingWidth = concentricRingWidth
        orbView.weeklyArcWidth = weeklyArcWidth
        panel.contentView = orbView
        restorePosition()
        panel.orderFrontRegardless()

        refresh()
        refreshTimer = Timer.scheduledTimer(withTimeInterval: 15, repeats: true) { [weak self] _ in self?.refresh() }
    }

    func applicationWillTerminate(_ notification: Notification) {
        refreshTimer?.invalidate()
    }

    func windowDidMove(_ notification: Notification) {
        guard panel != nil else { return }
        defaults.set(Double(panel.frame.origin.x), forKey: "orbX")
        defaults.set(Double(panel.frame.origin.y), forKey: "orbY")
    }

    func orbView(_ view: OrbView, showMenuFor event: NSEvent) {
        let menu = NSMenu(title: localized(language, "Codex Usage Orb", "Codex 用量悬浮球"))
        menu.addItem(withTitle: localized(language, "Refresh now", "立即刷新"), action: #selector(refreshFromMenu), keyEquivalent: "")
        menu.addItem(withTitle: localized(language, "Appearance settings…", "外观设置…"), action: #selector(showAppearance), keyEquivalent: "")
        menu.addItem(.separator())
        menu.addItem(withTitle: localized(language, "Quit", "退出"), action: #selector(quit), keyEquivalent: "")
        for item in menu.items { item.target = self }
        NSMenu.popUpContextMenu(menu, with: event, for: view)
    }

    @objc private func refreshFromMenu() { refresh() }
    @objc private func quit() { NSApplication.shared.terminate(nil) }

    @objc private func showAppearance() {
        let originalSize = size
        let originalAccent = accent
        let originalWeeklyAccent = weeklyAccent
        let originalLanguage = language
        let originalStyle = style
        let originalConcentricRingWidth = concentricRingWidth
        let originalWeeklyArcWidth = weeklyArcWidth
        let controls = AppearanceControls(size: size, concentricRingWidth: concentricRingWidth, weeklyArcWidth: weeklyArcWidth, color: accent, weeklyColor: weeklyAccent, language: language, style: style)
        controls.onChange = { [weak self] newSize, newRingWidth, newArcWidth, newColor, newWeeklyColor, newLanguage, newStyle in
            self?.apply(size: newSize, concentricRingWidth: newRingWidth, weeklyArcWidth: newArcWidth, accent: newColor, weeklyAccent: newWeeklyColor, language: newLanguage, style: newStyle)
        }

        let alert = NSAlert()
        alert.messageText = localized(language, "Adjust style, size, width, color, and language", "实时调整样式、大小、宽度、颜色和语言")
        alert.informativeText = localized(language,
            "Size: 50–300 px. Meter widths: 2–14 px. Changes are previewed immediately.",
            "尺寸范围为 50–300 px，环与周弧宽度范围为 2–14 px，修改会立即预览。")
        alert.accessoryView = controls.view
        alert.addButton(withTitle: localized(language, "OK", "确定"))
        alert.addButton(withTitle: localized(language, "Cancel", "取消"))
        let response = alert.runModal()
        if response == .alertFirstButtonReturn {
            defaults.set(Double(size), forKey: "orbSize")
            defaults.set(accent.hexString, forKey: "accent")
            defaults.set(weeklyAccent.hexString, forKey: "weeklyAccent")
            defaults.set(language, forKey: "language")
            defaults.set(style.rawValue, forKey: "orbStyle")
            defaults.set(Double(concentricRingWidth), forKey: "concentricRingWidth")
            defaults.set(Double(weeklyArcWidth), forKey: "weeklyArcWidth")
        } else {
            apply(size: originalSize, concentricRingWidth: originalConcentricRingWidth, weeklyArcWidth: originalWeeklyArcWidth, accent: originalAccent, weeklyAccent: originalWeeklyAccent, language: originalLanguage, style: originalStyle)
        }
    }

    private func apply(size newSize: CGFloat, concentricRingWidth newConcentricRingWidth: CGFloat, weeklyArcWidth newWeeklyArcWidth: CGFloat, accent newAccent: NSColor, weeklyAccent newWeeklyAccent: NSColor, language newLanguage: String, style newStyle: OrbStyle) {
        size = max(50, min(300, newSize))
        concentricRingWidth = max(2, min(14, newConcentricRingWidth))
        weeklyArcWidth = max(2, min(14, newWeeklyArcWidth))
        accent = newAccent.usingColorSpace(.deviceRGB) ?? newAccent
        weeklyAccent = newWeeklyAccent.usingColorSpace(.deviceRGB) ?? newWeeklyAccent
        language = newLanguage == "zh" ? "zh" : "en"
        style = newStyle
        panel.setContentSize(NSSize(width: size, height: size))
        orbView.accent = accent
        orbView.weeklyAccent = weeklyAccent
        orbView.language = language
        orbView.style = style
        orbView.concentricRingWidth = concentricRingWidth
        orbView.weeklyArcWidth = weeklyArcWidth
        clampToVisibleScreen()
    }

    private func refresh() {
        DispatchQueue.global(qos: .utility).async { [weak self] in
            guard let self = self else { return }
            let result = self.reader.readLatest()
            DispatchQueue.main.async { self.orbView.snapshot = result }
        }
    }

    private func restorePosition() {
        let storedX = defaults.object(forKey: "orbX") as? Double
        let storedY = defaults.object(forKey: "orbY") as? Double
        if let x = storedX, let y = storedY {
            panel.setFrameOrigin(NSPoint(x: x, y: y))
            clampToVisibleScreen()
            return
        }
        guard let frame = NSScreen.main?.visibleFrame else { return }
        panel.setFrameOrigin(NSPoint(x: frame.maxX - size - 24, y: frame.maxY - size - 46))
    }

    private func clampToVisibleScreen() {
        guard let screen = panel.screen ?? NSScreen.main else { return }
        let visible = screen.visibleFrame
        let x = max(visible.minX, min(panel.frame.minX, visible.maxX - panel.frame.width))
        let y = max(visible.minY, min(panel.frame.minY, visible.maxY - panel.frame.height))
        panel.setFrameOrigin(NSPoint(x: x, y: y))
    }
}

private func localized(_ language: String, _ english: String, _ chinese: String) -> String {
    return language == "zh" ? chinese : english
}

private func windowName(_ minutes: Int, _ language: String) -> String {
    if (10_000...10_200).contains(minutes) { return localized(language, "Weekly limit", "周限额") }
    if (280...320).contains(minutes) { return localized(language, "5-hour limit", "5小时限额") }
    if minutes % 1_440 == 0 { return localized(language, "\(minutes / 1_440)-day limit", "\(minutes / 1_440)天限额") }
    if minutes % 60 == 0 { return localized(language, "\(minutes / 60)-hour limit", "\(minutes / 60)小时限额") }
    return localized(language, "\(minutes)-minute limit", "\(minutes)分钟限额")
}

private extension NSColor {
    convenience init?(hex: String) {
        let value = hex.trimmingCharacters(in: CharacterSet(charactersIn: "#"))
        guard value.count == 6, let number = Int(value, radix: 16) else { return nil }
        self.init(
            calibratedRed: CGFloat((number >> 16) & 0xff) / 255,
            green: CGFloat((number >> 8) & 0xff) / 255,
            blue: CGFloat(number & 0xff) / 255,
            alpha: 1
        )
    }

    func scaled(by factor: CGFloat) -> NSColor {
        guard let rgb = usingColorSpace(.deviceRGB) else { return self }
        return NSColor(
            calibratedRed: max(0, min(1, rgb.redComponent * factor)),
            green: max(0, min(1, rgb.greenComponent * factor)),
            blue: max(0, min(1, rgb.blueComponent * factor)),
            alpha: rgb.alphaComponent
        )
    }

    func mixed(with other: NSColor, weight: CGFloat) -> NSColor {
        guard let first = usingColorSpace(.deviceRGB), let second = other.usingColorSpace(.deviceRGB) else { return self }
        let ownWeight = 1 - weight
        return NSColor(
            calibratedRed: first.redComponent * ownWeight + second.redComponent * weight,
            green: first.greenComponent * ownWeight + second.greenComponent * weight,
            blue: first.blueComponent * ownWeight + second.blueComponent * weight,
            alpha: 1
        )
    }

    var hexString: String {
        guard let rgb = usingColorSpace(.deviceRGB) else { return "#61DC18" }
        return String(
            format: "#%02X%02X%02X",
            Int((rgb.redComponent * 255).rounded()),
            Int((rgb.greenComponent * 255).rounded()),
            Int((rgb.blueComponent * 255).rounded())
        )
    }
}

let application = NSApplication.shared
let applicationDelegate = AppDelegate()
application.setActivationPolicy(.accessory)
application.delegate = applicationDelegate
application.run()
