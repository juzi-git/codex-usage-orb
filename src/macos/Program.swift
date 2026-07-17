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

    var windows: [LimitWindow] {
        return [primary, secondary].compactMap { $0 }
    }

    var remainingPercent: Double {
        return windows.map(\.remainingPercent).min() ?? 0
    }
}

/// Reads only the tail of recent Codex rollout files. Authentication data and
/// conversation content are never retained or transmitted.
private final class CodexUsageReader {
    private let sessionsURL = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent(".codex/sessions", isDirectory: true)
    private let tailBytes: UInt64 = 1_048_576

    func readLatest() -> UsageSnapshot? {
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

        return candidates
            .sorted { $0.modified > $1.modified }
            .prefix(8)
            .compactMap { parseLatest(in: $0.url) }
            .max { $0.timestamp < $1.timestamp }
    }

    private func parseLatest(in url: URL) -> UsageSnapshot? {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? handle.close() }

        guard let length = try? handle.seekToEnd() else { return nil }
        let start = length > tailBytes ? length - tailBytes : 0
        try? handle.seek(toOffset: start)
        guard let data = try? handle.readToEnd(),
              let text = String(data: data, encoding: .utf8) else { return nil }

        for line in text.split(separator: "\n", omittingEmptySubsequences: true).reversed() {
            guard line.contains("\"rate_limits\"") else { continue }
            if let snapshot = parseLine(Data(line.utf8)) { return snapshot }
        }
        return nil
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
            planType: rateLimits["plan_type"] as? String
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
    var language = "en" {
        didSet { updateToolTip(); needsDisplay = true }
    }
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

        NSGraphicsContext.saveGraphicsState()
        circle.addClip()
        let remaining = CGFloat(snapshot?.remainingPercent ?? 50) / 100
        drawWave(in: circleRect, fill: remaining, phase: phase + 1.8, amplitude: 0.033, wavelength: 0.20,
                 top: accent.withAlphaComponent(0.73), bottom: accent.scaled(by: 0.55).withAlphaComponent(0.82))
        drawWave(in: circleRect, fill: remaining, phase: phase, amplitude: 0.043, wavelength: 0.25,
                 top: accent, bottom: accent.scaled(by: 0.42))
        NSGraphicsContext.restoreGraphicsState()

        drawLabels(in: circleRect)
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

    private func drawLabels(in rect: NSRect) {
        let percent = snapshot.map { "\(Int($0.remainingPercent.rounded()))%" } ?? "--"
        let active = snapshot?.windows.min { $0.remainingPercent < $1.remainingPercent }
        let subtitle = active.map {
            localized(language, "\(windowName($0.windowMinutes, language)) remaining", "\(windowName($0.windowMinutes, language))剩余")
        } ?? localized(language, "No data", "暂无数据")

        let percentFont = NSFont.systemFont(ofSize: rect.width * 0.27, weight: .semibold)
        let percentAttributes: [NSAttributedString.Key: Any] = [
            .font: percentFont,
            .foregroundColor: NSColor.white
        ]
        let percentSize = percent.size(withAttributes: percentAttributes)
        percent.draw(
            at: NSPoint(x: rect.midX - percentSize.width / 2, y: rect.midY - percentSize.height * 0.28),
            withAttributes: percentAttributes
        )

        guard rect.width >= 72 else { return }
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
    let colorWell: NSColorWell
    let languagePopup: NSPopUpButton
    var onChange: ((CGFloat, NSColor, String) -> Void)?

    init(size: CGFloat, color: NSColor, language: String) {
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

        let colorRow = NSStackView()
        colorRow.orientation = .horizontal
        colorRow.spacing = 6
        let presetNames = language == "zh"
            ? [("绿色", "#61DC18"), ("蓝色", "#37BEFF"), ("紫色", "#A970FF"), ("橙色", "#FF9F35")]
            : [("Green", "#61DC18"), ("Blue", "#37BEFF"), ("Purple", "#A970FF"), ("Orange", "#FF9F35")]
        for preset in presetNames {
            let button = NSButton(title: preset.0, target: nil, action: nil)
            button.identifier = NSUserInterfaceItemIdentifier(preset.1)
            button.bezelStyle = .rounded
            colorRow.addArrangedSubview(button)
        }
        colorWell = NSColorWell(frame: NSRect(x: 0, y: 0, width: 48, height: 28))
        colorWell.color = color
        colorRow.addArrangedSubview(colorWell)

        container.addArrangedSubview(languageRow)
        container.addArrangedSubview(sizeRow)
        container.addArrangedSubview(colorRow)
        view = container
        super.init()

        slider.target = self
        slider.action = #selector(valueChanged)
        colorWell.target = self
        colorWell.action = #selector(valueChanged)
        languagePopup.target = self
        languagePopup.action = #selector(valueChanged)
        for case let button as NSButton in colorRow.arrangedSubviews {
            button.target = self
            button.action = #selector(presetClicked(_:))
        }

        NSLayoutConstraint.activate([
            container.widthAnchor.constraint(equalToConstant: 320),
            slider.widthAnchor.constraint(equalToConstant: 190)
        ])
    }

    @objc private func valueChanged() {
        sizeLabel.stringValue = "\(Int(slider.doubleValue.rounded())) px"
        onChange?(CGFloat(slider.doubleValue), colorWell.color, languagePopup.indexOfSelectedItem == 1 ? "zh" : "en")
    }

    @objc private func presetClicked(_ sender: NSButton) {
        guard let value = sender.identifier?.rawValue, let color = NSColor(hex: value) else { return }
        colorWell.color = color
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
    private var language = "en"

    func applicationDidFinishLaunching(_ notification: Notification) {
        let storedSize = defaults.double(forKey: "orbSize")
        size = storedSize == 0 ? 168 : CGFloat(max(50, min(300, storedSize)))
        if let hex = defaults.string(forKey: "accent"), let color = NSColor(hex: hex) { accent = color }
        language = defaults.string(forKey: "language") == "zh" ? "zh" : "en"

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
        orbView.language = language
        panel.contentView = orbView
        restorePosition()
        panel.orderFrontRegardless()

        refresh()
        refreshTimer = Timer.scheduledTimer(withTimeInterval: 5, repeats: true) { [weak self] _ in self?.refresh() }
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
        let originalLanguage = language
        let controls = AppearanceControls(size: size, color: accent, language: language)
        controls.onChange = { [weak self] newSize, newColor, newLanguage in
            self?.apply(size: newSize, accent: newColor, language: newLanguage)
        }

        let alert = NSAlert()
        alert.messageText = localized(language, "Adjust size, color, and language", "实时调整大小、颜色和语言")
        alert.informativeText = localized(language,
            "Choose a size from 50 to 300 px. Changes are previewed immediately.",
            "尺寸范围为 50–300 px，修改会立即预览。")
        alert.accessoryView = controls.view
        alert.addButton(withTitle: localized(language, "OK", "确定"))
        alert.addButton(withTitle: localized(language, "Cancel", "取消"))
        let response = alert.runModal()
        if response == .alertFirstButtonReturn {
            defaults.set(Double(size), forKey: "orbSize")
            defaults.set(accent.hexString, forKey: "accent")
            defaults.set(language, forKey: "language")
        } else {
            apply(size: originalSize, accent: originalAccent, language: originalLanguage)
        }
    }

    private func apply(size newSize: CGFloat, accent newAccent: NSColor, language newLanguage: String) {
        size = max(50, min(300, newSize))
        accent = newAccent.usingColorSpace(.deviceRGB) ?? newAccent
        language = newLanguage == "zh" ? "zh" : "en"
        panel.setContentSize(NSSize(width: size, height: size))
        orbView.accent = accent
        orbView.language = language
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
