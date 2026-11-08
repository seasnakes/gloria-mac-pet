import AppKit

// GLORIA · 柔彩 — a desktop companion for macOS, inspired by Capri (github.com/lachydotmcg/capri-desktop).

let arguments = CommandLine.arguments
if let i = arguments.firstIndex(of: "--hook") {
    HookClient.run(source: i + 1 < arguments.count ? arguments[i + 1] : "claude")
    exit(0)
}
if let i = arguments.firstIndex(of: "--hooks") {
    // GLORIA --hooks claude|codex on|off   (same as the menu toggles)
    let src = i + 1 < arguments.count ? arguments[i + 1] : "claude"
    let on = i + 2 < arguments.count ? arguments[i + 2] != "off" : true
    do { try HookInstaller.set(src, on: on); print("\(on ? "已开启" : "已关闭") \(src)：\(HookInstaller.file(src).path)"); exit(0) }
    catch { print("失败：\(error)"); exit(1) }
}
if let i = arguments.firstIndex(of: "--ctl") {
    exit(HookClient.control(i + 1 < arguments.count ? arguments[i + 1] : "{\"ctl\":\"status\"}"))
}

let app = NSApplication.shared
let appDelegate = AppDelegate()
app.delegate = appDelegate
app.setActivationPolicy(.accessory)
app.run()
