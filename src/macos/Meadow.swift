import AppKit
import WebKit

final class Meadow: NSObject, NSApplicationDelegate, NSWindowDelegate, WKNavigationDelegate {
    let preview = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1100, height: 690),
                           styleMask: [.titled, .closable, .resizable, .miniaturizable], backing: .buffered, defer: false)
    let desktop = NSWindow(contentRect: .zero, styleMask: .borderless, backing: .buffered, defer: true)
    let web = WKWebView()
    var tray: NSStatusItem!
    var desktopItem: NSMenuItem!
    var monitor: Any?
    var painting = false
    var plainClick = false
    var mounted = false
    var lastDrag: TimeInterval = 0
    var checkingReload = false

    func applicationDidFinishLaunching(_ notification: Notification) {
        guard let page = Bundle.main.url(forResource: "flower-meadow", withExtension: "html") else {
            NSApp.terminate(nil)
            return
        }
        let smoke = ProcessInfo.processInfo.arguments.contains("--smoke")
        checkingReload = ProcessInfo.processInfo.arguments.contains("--restore")
        tray = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        tray.button?.image = NSImage(systemSymbolName: "leaf", accessibilityDescription: "野花原")
        let menu = NSMenu()
        for (title, selector, shortcut) in [
            ("打开试玩窗口", #selector(showPreview), ""),
            ("挂到桌面（试验）", #selector(toggleDesktop), ""),
            ("预览一天 · 45 秒", #selector(previewDay), ""),
            ("退出野花原", #selector(quit), "q")
        ] {
            let item = menu.addItem(withTitle: title, action: selector, keyEquivalent: shortcut)
            item.target = self
            if selector == #selector(toggleDesktop) { desktopItem = item }
        }
        tray.button?.toolTip = "野花原 · 左键打开种子篮，选种后左键种花"
        tray.menu = menu
        preview.title = "野花原 · 互动壁纸"
        preview.minSize = NSSize(width: 360, height: 280)
        preview.isReleasedWhenClosed = false
        preview.delegate = self
        preview.center()
        desktop.level = NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.desktopIconWindow)) - 1)
        desktop.collectionBehavior = [.canJoinAllSpaces, .stationary, .ignoresCycle]
        desktop.ignoresMouseEvents = true
        desktop.hasShadow = false
        desktop.isReleasedWhenClosed = false
        if smoke || checkingReload || ProcessInfo.processInfo.arguments.contains("--desktop") || ProcessInfo.processInfo.arguments.contains("--light-check") { web.navigationDelegate = self }
        showPreview()
        web.loadFileURL(page, allowingReadAccessTo: page.deletingLastPathComponent())
    }

    func host(_ window: NSWindow) {
        web.removeFromSuperview()
        web.autoresizingMask = [.width, .height]
        web.frame = window.contentView?.bounds ?? .zero
        window.contentView?.addSubview(web)
    }

    @objc func showPreview() {
        unmount()
        host(preview)
        preview.makeKeyAndOrderFront(nil)
        action("visible", 1)
        NSApp.activate(ignoringOtherApps: true)
    }

    @objc func toggleDesktop() {
        if mounted { showPreview(); return }
        guard let screen = preview.screen ?? NSScreen.main else { return }
        mounted = true
        desktopItem.title = "退出桌面模式"
        preview.orderOut(nil)
        desktop.setFrame(screen.frame, display: true)
        host(desktop)
        action("wallpaper", 1)
        desktop.orderBack(nil)
        action("visible", 1)
        monitor = NSEvent.addGlobalMonitorForEvents(matching: [.leftMouseDown, .leftMouseDragged, .leftMouseUp]) {
            [weak self] event in self?.desktopEvent(event)
        }

    }

    func unmount() {
        guard mounted else { return }
        stopPainting()
        if let monitor { NSEvent.removeMonitor(monitor) }
        monitor = nil
        desktop.orderOut(nil)
        mounted = false
        desktopItem.title = "挂到桌面（试验）"
        action("wallpaper", 0)
    }

    func stopPainting() {
        if painting { action("up"); painting = false }
    }

    func action(_ name: String, _ x: CGFloat = 0, _ y: CGFloat = 0) {
        web.evaluateJavaScript("window.meadowDesktop?.('\(name)', \(x), \(y))")
    }

    func desktopEvent(_ event: NSEvent) {
        if event.type == .leftMouseUp { stopPainting(); return }
        let modifiers = event.modifierFlags.intersection([.option, .control, .command, .shift])
        guard mounted, modifiers.isEmpty || modifiers == .option,
              let point = event.cgEvent?.location else { stopPainting(); return }
        if event.type == .leftMouseDragged {
            guard painting, event.timestamp - lastDrag >= 1.0 / 30 else { return }
        }
        let location = NSEvent.mouseLocation
        guard desktop.frame.contains(location),
              desktop.screen?.visibleFrame.contains(location) == true,
              exposedDesktop(at: point) else { stopPainting(); return }
        let x = location.x - desktop.frame.minX, y = desktop.frame.maxY - location.y
        if event.type == .leftMouseDown {
            painting = true
            plainClick = modifiers.isEmpty
            lastDrag = event.timestamp
            action(plainClick ? "plainDown" : "down", x, y)
        } else {
            guard plainClick == modifiers.isEmpty else { stopPainting(); return }
            lastDrag = event.timestamp
            action("drag", x, y)
        }
    }

    func exposedDesktop(at point: CGPoint) -> Bool {
        guard let windows = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements],
                                                       kCGNullWindowID) as? [[String: Any]] else { return false }
        let dockPID = NSRunningApplication.runningApplications(withBundleIdentifier: "com.apple.dock").first?.processIdentifier
        return !windows.contains { window in
            guard let layer = window[kCGWindowLayer as String] as? Int, layer >= 0,
                  (window[kCGWindowAlpha as String] as? Double ?? 1) > 0,
                  let bounds = window[kCGWindowBounds as String] as? NSDictionary,
                  let rect = CGRect(dictionaryRepresentation: bounds) else { return false }
            // Dock can report a display-sized transparent layer. Its controls remain outside visibleFrame.
            if let dockPID, window[kCGWindowOwnerPID as String] as? pid_t == dockPID,
               layer > 0, NSScreen.screens.contains(where: { $0.frame.size == rect.size }) { return false }
            return rect.contains(point)
        }
    }

    func windowWillClose(_ notification: Notification) {
        if !mounted { action("visible", 0) }
    }

    @objc func previewDay() { action("tour") }

    @objc func quit() { unmount(); NSApp.terminate(nil) }

    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        if ProcessInfo.processInfo.arguments.contains("--desktop") {
            webView.navigationDelegate = nil
            toggleDesktop()
            if ProcessInfo.processInfo.arguments.contains("--tour") { previewDay() }
            return
        }
        if ProcessInfo.processInfo.arguments.contains("--light-check") {
            webView.navigationDelegate = nil
            captureLight([6, 10, 14, 18, 22])
            return
        }
        if checkingReload {
            web.evaluateJavaScript("""
                (() => {
                  const s = JSON.parse(localStorage.getItem('pond-garden-v2'));
                  return s.flowers.length >= 2 && s.vase.length === 1 && !s.held && s.lanterns.length === 1 &&
                    document.querySelector('#vase span').textContent.includes('1') &&
                    document.querySelector('#effects').value === 'still';
                })()
                """) { result, error in
                guard error == nil, result as? Bool == true else {
                    fputs("Reload did not restore garden, vase, and effects\n", stderr); exit(1)
                }
                print("WebKit passed: garden, vase, lantern, and settings restored.")
                // A forced process exit can interrupt WebKit’s asynchronous storage flush.
                DispatchQueue.main.asyncAfter(deadline: .now() + 2) { NSApp.terminate(nil) }
            }
            return
        }
        let script = """
        (() => {
          const $ = id => document.getElementById(id), field = $('field');
          const state = () => JSON.parse(localStorage.getItem('pond-garden-v2'));
          const point = (event,x,y) => field.dispatchEvent(new PointerEvent(event,
            {bubbles:true,pointerId:33,pointerType:'mouse',clientX:x*innerWidth,clientY:y*innerHeight,button:0}));
          const click = (x,y) => { point('pointerdown',x,y); point('pointerup',x,y); };
          if (!$('seeds-panel').hidden) throw Error('Basket should start closed');
          $('settings').click();
          $('effects').value='still'; $('effects').dispatchEvent(new Event('change'));
          $('fast').click(); $('settings').click();
          click(.60,.46);
          if (data.flowers.length) throw Error('Water accepted seeds');
          meadowDesktop('wallpaper',1,0);
          meadowDesktop('plainDown',.29*innerWidth,.75*innerHeight);meadowDesktop('up');
          if (data.flowers.length) throw Error('Idle desktop click planted a flower');
          const desktopClick = element => {
            const r=element.getBoundingClientRect();
            meadowDesktop('plainDown',r.x+r.width/2,r.y+r.height/2);meadowDesktop('up');
          };
          desktopClick($('basket'));
          if ($('seeds-panel').hidden) throw Error('Desktop did not open basket');
          desktopClick($('rack').children[1]);
          if (!$('seeds-panel').hidden || $('rack').children[1].getAttribute('aria-pressed')!=='true')
            throw Error('Desktop did not pick seed and close basket');
          meadowDesktop('plainDown',.60*innerWidth,.46*innerHeight);meadowDesktop('up');
          if (data.flowers.length) throw Error('Desktop planting accepted water');
          meadowDesktop('plainDown',.29*innerWidth,.75*innerHeight);
          meadowDesktop('drag',.67*innerWidth,.87*innerHeight);
          meadowDesktop('up');
          if (state().flowers.length<3 || state().flowers.some(f=>f.kind!==1))
            throw Error('Desktop bridge did not sow chosen seeds');
          const count=state().flowers.length;
          desktopClick($('planting-done'));
          meadowDesktop('plainDown',.11*innerWidth,.52*innerHeight);meadowDesktop('up');
          if (state().flowers.length!==count) throw Error('Finished planting mode still sowed');
          meadowDesktop('wallpaper',0,0);
          point('pointerdown',.29,.75); point('pointermove',.67,.87); point('pointerup',.67,.87);
          if (state().flowers.length < 3 || state().flowers[0].kind !== 1) throw Error('Drag did not sow selected seeds');
          $('basket').click(); $('shears').click(); click(.29,.75);
          if (state().held || !$('status').textContent.includes('还没开花')) throw Error('Immature plant could be cut');
          return true;
        })()
        """
        web.evaluateJavaScript(script) { [weak self] value, error in
            guard let self, value as? Bool == true, error == nil else {
                fputs("Smoke failed: \(String(describing: error))\n", stderr); exit(1)
            }
            DispatchQueue.main.asyncAfter(deadline: .now() + 6.3) {
                self.web.evaluateJavaScript("""
                (() => {
                  const state = () => JSON.parse(localStorage.getItem('pond-garden-v2'));
                  const before = state().flowers.length;
                  const beforePaint = [document.querySelector('#field').width,document.querySelector('#field').height];
                  if (beforePaint[0]*beforePaint[1]>1920*1080) throw Error('Canvas too large');
                  meadowDesktop('down',.29*innerWidth,.75*innerHeight);
                  meadowDesktop('up');
                  if (state().flowers.length !== before-1 || state().held?.kind !== 1) throw Error('Cutting failed');
                  document.querySelector('#vase').click();
                  if (state().vase.length !== 1 || state().held) throw Error('Vase rejected stem');
                  select(0);
                  const dated=new Date();dated.setHours(22,0,0,0);Date.now=()=>dated.getTime();
                  draw(performance.now());
                  const fly=visibleFlies.find(f=>f.x<innerWidth*.5 && f.y>innerHeight*.5);
                  if(!fly)throw Error('No visible firefly at night');
                  meadowDesktop('down',fly.x,fly.y);meadowDesktop('up');
                  if(carriedFly!==fly.i || state().lanterns.length)throw Error('Firefly capture failed');
                  meadowDesktop('down',.60*innerWidth,.46*innerHeight);meadowDesktop('up');
                  if(carriedFly!==fly.i || state().lanterns.length)throw Error('Lantern was placed in water');
                  const x=.19*innerWidth,y=.72*innerHeight;
                  meadowDesktop('down',x,y);meadowDesktop('up');
                  draw(performance.now());
                  if(carriedFly!==null||state().lanterns.length!==1)
                    throw Error('Lantern did not persist');
                  dated.setHours(14);draw(performance.now());
                  if(visibleFlies.length)throw Error('Daytime fireflies still visible');
                  dated.setHours(22);draw(performance.now());
                  return true;
                })()
                """) { value,error in
                    guard value as? Bool == true, error == nil else {
                        fputs("Smoke failed after growth: \(String(describing: error))\n",stderr); exit(1)
                    }
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { self.captureAndReload() }
                }
            }
        }
    }

    func captureLight(_ hours: [Int]) {
        guard let hour = hours.first else { print("Light captures: 06, 10, 14, 18, 22."); exit(0) }
        let js = """
        (() => {
          if (!document.querySelector('#tour')) return false;
          const real = new Date();
          if (sceneHour(real.getTime()) < 0 || Math.floor(sceneHour(real.getTime())) !== real.getHours()) return false;
          if (!window.__wallClock) window.__wallClock = Date.now.bind(Date);
          const fixed = new Date(); fixed.setHours(\(hour), 0, 0, 0);
          Date.now = () => fixed.getTime();
          const a=fireflyState(fireflies[0],0), b=fireflyState(fireflies[0],1);
          if (a.x===b.x || a.y===b.y || a.blink===b.blink || reflectedStars.length<15) return false;
          requestDraw();
          return Math.floor(sceneHour(Date.now())) === \(hour) &&
            botanical.naturalWidth === 512 && wild.length >= 300 && field.width * field.height <= 1920 * 1080;
        })()
        """
        web.evaluateJavaScript(js) { result, error in
            guard error == nil, result as? Bool == true else {
                fputs("Light check failed: \(String(describing: error))\n", stderr); exit(1)
            }
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) {
                let config = WKSnapshotConfiguration()
                config.snapshotWidth = 740
                self.web.takeSnapshot(with: config) { image, error in
                    guard let tiff = image?.tiffRepresentation, let bitmap = NSBitmapImageRep(data: tiff),
                          let png = bitmap.representation(using: .png, properties: [:]), error == nil,
                          let destination = ProcessInfo.processInfo.environment["MEADOW_CAPTURE_DIR"] else { exit(1) }
                    do {
                        try png.write(to: URL(fileURLWithPath: destination).appendingPathComponent("pond-\(hour).png"))
                        self.captureLight(Array(hours.dropFirst()))
                    } catch { fputs("\(error)\n", stderr); exit(1) }
                }
            }
        }
    }

    func captureAndReload() {
        let config = WKSnapshotConfiguration()
        config.snapshotWidth = 1100
        web.takeSnapshot(with: config) { image, error in
            guard let tiff = image?.tiffRepresentation, let bitmap = NSBitmapImageRep(data: tiff),
                  let png = bitmap.representation(using: .png, properties: [:]), error == nil,
                  let destination = ProcessInfo.processInfo.environment["MEADOW_CAPTURE"] else { exit(1) }
            do {
                try png.write(to: URL(fileURLWithPath: destination))
                self.checkingReload = true
                self.web.reload()
            } catch { fputs("\(error)\n",stderr); exit(1) }
        }
    }
}

let app = NSApplication.shared
let delegate = Meadow()
app.setActivationPolicy(.accessory)
app.delegate = delegate
app.run()
