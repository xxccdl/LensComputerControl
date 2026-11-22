// hud-mac.swift —— macOS 的「AI 正在操作」浮层（对应 Windows 的 lenshud.exe）。
//
// 为什么是这个写法：macOS 上没法从 Node/JXA 画窗口，只能起一个 AppKit 进程。
// 这个程序没有第三方依赖，只做三件事：
//   1. 每块屏幕上盖一层**点击穿透**的渐变描边（屏幕边缘的呼吸光）
//   2. 顶部中间显示一枚胶囊，写着当前动作（如 "scan 微信 · 23 候选"）
//   3. 轮询一个状态文件（引擎写、这里读），有变化就闪一下并在 2.8s 后淡出
//
// 用「文件 + 轮询」而不是 socket：少一整层 IPC 代码，失败面小得多。
//
// 用法：
//   swiftc -O hud-mac.swift -o hud-mac
//   ./hud-mac --state /tmp/lens-hud-mac.json
//
// 状态文件（引擎写）：
//   {"seq":12,"text":"scan 微信 · 23 候选","kind":"scan","hide":false,"quit":false}

import AppKit
import Foundation

let ACCENT_TOP = NSColor(srgbRed: 56 / 255, green: 140 / 255, blue: 248 / 255, alpha: 1)
let ACCENT_BOTTOM = NSColor(srgbRed: 99 / 255, green: 102 / 255, blue: 241 / 255, alpha: 1)
let CAPSULE_BG = NSColor(srgbRed: 0, green: 0, blue: 0, alpha: 0.86)
let CAPSULE_LINE = NSColor(srgbRed: 56 / 255, green: 140 / 255, blue: 248 / 255, alpha: 0.7)
let TEXT_COLOR = NSColor(srgbRed: 226 / 255, green: 240 / 255, blue: 255 / 255, alpha: 1)
let DOT_COLOR = NSColor(srgbRed: 125 / 255, green: 211 / 255, blue: 252 / 255, alpha: 1)

struct HudState: Decodable {
    var seq: Int?
    var text: String?
    var kind: String?
    var hide: Bool?
    var quit: Bool?
}

final class OverlayView: NSView {
    var state = HudState(seq: 0, text: nil, kind: nil, hide: true, quit: false)
    var shownAt: Date = .distantPast
    let fadeAfter: TimeInterval = 2.8

    override var isOpaque: Bool { false }
    override var acceptsFirstResponder: Bool { false }

    /// 当前整体透明度：显示后 2.8s 内 1→0 线性淡出
    var alpha: CGFloat {
        let age = Date().timeIntervalSince(shownAt)
        if age < 0 || state.hide == true { return 0 }
        if age > fadeAfter { return 0 }
        if age > fadeAfter - 0.5 { return CGFloat((fadeAfter - age) / 0.5) }
        return 1
    }

    override func draw(_ dirtyRect: NSRect) {
        let a = alpha
        guard a > 0.01 else { return }
        let bounds = self.bounds

        // ── 屏幕边缘描边：四边用渐变填充，越靠中间越透明（模拟发光）
        let thickness: CGFloat = 34
        drawEdgeGradient(in: NSRect(x: 0, y: bounds.height - thickness, width: bounds.width, height: thickness),
                         from: NSPoint(x: 0, y: bounds.height), to: NSPoint(x: bounds.width, y: bounds.height),
                         vertical: true, alpha: a)
        drawEdgeGradient(in: NSRect(x: 0, y: 0, width: bounds.width, height: thickness),
                         from: NSPoint(x: 0, y: 0), to: NSPoint(x: bounds.width, y: 0),
                         vertical: true, alpha: a)
        drawEdgeGradient(in: NSRect(x: 0, y: 0, width: thickness, height: bounds.height),
                         from: NSPoint(x: 0, y: 0), to: NSPoint(x: 0, y: bounds.height),
                         vertical: false, alpha: a)
        drawEdgeGradient(in: NSRect(x: bounds.width - thickness, y: 0, width: thickness, height: bounds.height),
                         from: NSPoint(x: bounds.width, y: 0), to: NSPoint(x: bounds.width, y: bounds.height),
                         vertical: false, alpha: a)

        // ── 顶部胶囊
        let text = (state.text?.isEmpty == false) ? state.text! : "Lens 正在操作"
        let font = NSFont(name: "PingFang SC", size: 13) ?? NSFont.systemFont(ofSize: 13)
        let attrs: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: TEXT_COLOR.withAlphaComponent(a)]
        let textSize = (text as NSString).size(withAttributes: attrs)
        let dotW: CGFloat = 10
        let padH: CGFloat = 16
        let capH: CGFloat = 34
        let capW = min(bounds.width - 40, textSize.width + dotW + padH * 2 + 14)
        let capRect = NSRect(x: (bounds.width - capW) / 2, y: bounds.height - capH - 22, width: capW, height: capH)
        let path = NSBezierPath(roundedRect: capRect, xRadius: capH / 2, yRadius: capH / 2)
        CAPSULE_BG.withAlphaComponent(0.86 * a).setFill()
        path.fill()
        CAPSULE_LINE.withAlphaComponent(0.7 * a).setStroke()
        path.lineWidth = 1
        path.stroke()

        // 呼吸点
        let dotRect = NSRect(x: capRect.minX + padH, y: capRect.midY - dotW / 2, width: dotW, height: dotW)
        DOT_COLOR.withAlphaComponent(a).setFill()
        NSBezierPath(ovalIn: dotRect).fill()

        // 文本（超宽截断）
        let textRect = NSRect(x: dotRect.maxX + 8, y: capRect.midY - textSize.height / 2,
                              width: capW - padH * 2 - dotW - 8, height: textSize.height)
        (text as NSString).draw(in: textRect, withAttributes: attrs)
    }

    private func drawEdgeGradient(in rect: NSRect, from: NSPoint, to: NSPoint, vertical: Bool, alpha: CGFloat) {
        guard let ctx = NSGraphicsContext.current?.cgContext else { return }
        let colors = [ACCENT_TOP.withAlphaComponent(0.55 * alpha).cgColor,
                      ACCENT_BOTTOM.withAlphaComponent(0.35 * alpha).cgColor,
                      ACCENT_TOP.withAlphaComponent(0.0 * alpha).cgColor]
        let space = CGColorSpaceCreateDeviceRGB()
        guard let gradient = CGGradient(colorsSpace: space, colors: colors as CFArray, locations: [0, 0.55, 1]) else { return }
        ctx.saveGState()
        ctx.clip(to: rect)
        // 从边缘向内侧淡出
        let start: CGPoint
        let end: CGPoint
        if rect.maxY >= bounds.height - 1 {
            start = CGPoint(x: rect.midX, y: rect.maxY)
            end = CGPoint(x: rect.midX, y: rect.minY)
        } else if rect.minY <= 1 {
            start = CGPoint(x: rect.midX, y: rect.minY)
            end = CGPoint(x: rect.midX, y: rect.maxY)
        } else if rect.minX <= 1 {
            start = CGPoint(x: rect.minX, y: rect.midY)
            end = CGPoint(x: rect.maxX, y: rect.midY)
        } else {
            start = CGPoint(x: rect.maxX, y: rect.midY)
            end = CGPoint(x: rect.minX, y: rect.midY)
        }
        ctx.drawLinearGradient(gradient, start: start, end: end, options: [.drawsBeforeStartLocation, .drawsAfterEndLocation])
        ctx.restoreGState()
    }
}

final class HudController: NSObject {
    var windows: [NSWindow] = []
    var views: [OverlayView] = []
    var statePath: String
    var lastSeq = -1
    var timer: Timer?

    init(statePath: String) {
        self.statePath = statePath
        super.init()
    }

    func build() {
        for screen in NSScreen.screens {
            let w = NSWindow(contentRect: screen.frame, styleMask: [.borderless], backing: .buffered, defer: false)
            w.isOpaque = false
            w.backgroundColor = .clear
            w.hasShadow = false
            w.ignoresMouseEvents = true
            w.level = NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.screenSaverWindow)))
            w.collectionBehavior = [.canJoinAllSpaces, .stationary, .fullScreenAuxiliary, .ignoresCycle]
            let v = OverlayView(frame: NSRect(origin: .zero, size: screen.frame.size))
            v.wantsLayer = true
            w.contentView = v
            w.setFrame(screen.frame, display: true)
            w.orderFrontRegardless()
            windows.append(w)
            views.append(v)
        }
    }

    func start() {
        build()
        timer = Timer.scheduledTimer(withTimeInterval: 0.12, repeats: true) { [weak self] _ in
            self?.poll()
        }
        RunLoop.main.add(timer!, forMode: .common)
    }

    func poll() {
        guard let data = FileManager.default.contents(atPath: statePath),
              let st = try? JSONDecoder().decode(HudState.self, from: data) else {
            redraw()
            return
        }
        if st.quit == true {
            NSApp.terminate(nil)
            return
        }
        let seq = st.seq ?? 0
        if seq != lastSeq {
            lastSeq = seq
            for v in views {
                v.state = st
                if st.hide != true {
                    v.shownAt = Date()
                }
            }
        }
        redraw()
    }

    func redraw() {
        for v in views { v.needsDisplay = true }
    }
}

// ---- 入口
var statePath = (NSTemporaryDirectory() as NSString).appendingPathComponent("lens-hud-mac.json")
var args = CommandLine.arguments
if let i = args.firstIndex(of: "--state"), i + 1 < args.count {
    statePath = args[i + 1]
}

let app = NSApplication.shared
app.setActivationPolicy(.accessory)

let controller = HudController(statePath: statePath)
controller.start()

// 让进程在后台也不被 App Nap 掐掉
ProcessInfo.processInfo.beginActivity(options: [.userInitiatedAllowingIdleSystemSleep, .suddenTerminationDisabled],
                                      reason: "Lens HUD overlay")

app.run()
