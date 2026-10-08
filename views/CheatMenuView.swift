import SwiftUI

// MARK: - AIM target / headshot enums

private enum AimTargetMode: Int, CaseIterable {
    case body = 0, head, mixed
    var label: String {
        switch self { case .body: return "BODY"; case .head: return "HEAD"; case .mixed: return "MIXED" }
    }
}

private enum HeadRate: Int, CaseIterable {
    case r0 = 0, r25, r50, r75, r100
    var label: String {
        switch self { case .r0: return "0%"; case .r25: return "25%";
        case .r50: return "50%"; case .r75: return "75%"; case .r100: return "100%" }
    }
}

private enum AimSystemTarget: Int, CaseIterable {
    case neck = 0, head
    var label: String { self == .head ? "HEAD" : "NECK" }
}

private enum SpeedLevel: Int, CaseIterable {
    case lv1 = 0, lv2, lv3
    var label: String { "Lv\(rawValue + 1)" }
}

// MARK: - Main view

struct CheatMenuView: View {

    // ── Chọn game ──────────────────────────────────────────────────
    @AppStorage("cheat.game") private var selectedGame: String = CheatGame.freeFire.rawValue

    // ── ESP ────────────────────────────────────────────────────────
    @AppStorage("esp.master")   private var espMaster   = true
    @AppStorage("esp.box")      private var espBox      = true
    @AppStorage("esp.tracer")   private var espTracer   = true
    @AppStorage("esp.health")   private var espHealth   = true
    @AppStorage("esp.name")     private var espName     = true
    @AppStorage("esp.distance") private var espDistance = true
    @AppStorage("esp.weapon")   private var espWeapon   = true

    // ── AIM ────────────────────────────────────────────────────────
    @AppStorage("aim.silent")     private var silentAim      = false
    @AppStorage("aim.system")     private var aimSystem      = false
    @AppStorage("aim.targetMode") private var aimTargetRaw   = AimTargetMode.mixed.rawValue
    @AppStorage("aim.headRate")   private var headRateRaw    = HeadRate.r75.rawValue
    @AppStorage("aim.fov")        private var aimFov         = false
    @AppStorage("aim.noRecoil")   private var noRecoil       = false
    @AppStorage("aim.sysTarget")  private var aimSysTargetRaw = AimSystemTarget.neck.rawValue

    // ── SETTINGS ───────────────────────────────────────────────────
    @AppStorage("settings.parachute") private var fastParachute = false
    @AppStorage("settings.speed")     private var speedRunning  = false
    @AppStorage("settings.speedLv")   private var speedLevelRaw = SpeedLevel.lv2.rawValue

    // ── UI state ───────────────────────────────────────────────────
    @State private var menuTab = 0          // 0 = Menu, 1 = Log
    @State private var isInjecting = false
    @State private var logLines: [String]  = []
    @State private var injectDone  = false
    @State private var injectError: String? = nil
    // After a successful inject, store docs path + bundleID for FileBrowserView
    @State private var injectedDocsPath: String?  = nil
    @State private var injectedBundleID: String?  = nil
    @State private var showFileBrowser = false

    private var game: CheatGame {
        CheatGame(rawValue: selectedGame) ?? .freeFire
    }

    // MARK: Body

    var body: some View {
        ZStack(alignment: .bottom) {
            Color(red: 0.07, green: 0.07, blue: 0.08).ignoresSafeArea()

            VStack(spacing: 0) {
                menuTabPicker
                    .padding(.horizontal, 16)
                    .padding(.top, 12)
                    .padding(.bottom, 8)

                if menuTab == 0 {
                    menuContent
                } else {
                    logContent
                }
            }

            injectBar
        }
    }

    // MARK: - Menu / Log tab picker

    private var menuTabPicker: some View {
        HStack(spacing: 0) {
            tabPickerButton("Menu", index: 0)
            tabPickerButton("Log",  index: 1)
        }
        .background(Color(red: 0.14, green: 0.14, blue: 0.16), in: RoundedRectangle(cornerRadius: 10))
    }

    private func tabPickerButton(_ title: String, index: Int) -> some View {
        Button { menuTab = index } label: {
            Text(title)
                .font(.system(size: 15, weight: .semibold))
                .frame(maxWidth: .infinity)
                .padding(.vertical, 8)
                .background(
                    menuTab == index
                        ? Color(red: 0.24, green: 0.24, blue: 0.26)
                            .clipShape(RoundedRectangle(cornerRadius: 8))
                        : nil
                )
                .foregroundStyle(menuTab == index ? .white : Color(white: 0.55))
        }
        .buttonStyle(.plain)
        .padding(3)
    }

    // MARK: - Menu content

    private var menuContent: some View {
        ScrollView {
            VStack(spacing: 20) {
                gamePickerSection
                espSection
                aimSection
                settingsSection
                Spacer(minLength: 96) // clearance for inject bar
            }
            .padding(.horizontal, 14)
            .padding(.top, 8)
        }
    }

    // MARK: Game picker

    private var gamePickerSection: some View {
        VStack(alignment: .leading, spacing: 6) {
            sectionHeader("GAME MỤC TIÊU")
            VStack(spacing: 0) {
                ForEach(Array(CheatGame.allCases.enumerated()), id: \.element) { idx, g in
                    Button {
                        selectedGame = g.rawValue
                    } label: {
                        HStack(spacing: 14) {
                            iconBox(systemName: "gamecontroller.fill",
                                    color: g == .freeFire
                                        ? Color(red: 0.99, green: 0.35, blue: 0.19)
                                        : Color(red: 0.20, green: 0.65, blue: 1.00))

                            Text(g.displayName)
                                .font(.system(size: 16, weight: .semibold))
                                .foregroundStyle(.white)

                            Spacer()

                            Image(systemName: game == g
                                  ? "checkmark.circle.fill" : "circle")
                                .font(.system(size: 20))
                                .foregroundStyle(game == g
                                    ? Color(red: 0.99, green: 0.35, blue: 0.19)
                                    : Color(white: 0.35))
                        }
                        .padding(.horizontal, 14)
                        .padding(.vertical, 13)
                    }
                    .buttonStyle(.plain)

                    if idx < CheatGame.allCases.count - 1 { rowDivider }
                }
            }
            .background(rowBg)
        }
    }

    // MARK: ESP section

    private var espSection: some View {
        VStack(alignment: .leading, spacing: 6) {
            sectionHeader("ESP")
            VStack(spacing: 0) {
                toggleRow(icon: "eye.fill",           iconColor: Color(red: 0.40, green: 0.82, blue: 0.55),
                          title: "Enable ESP",         subtitle: "Bật/tắt toàn bộ ESP",     binding: $espMaster)
                rowDivider
                toggleRow(icon: "rectangle.dashed",   iconColor: Color(red: 0.40, green: 0.72, blue: 1.00),
                          title: "Player Box",         subtitle: "Khung xung quanh kẻ địch", binding: $espBox)
                    .disabled(!espMaster)
                rowDivider
                toggleRow(icon: "arrow.down.to.line", iconColor: Color(red: 0.95, green: 0.70, blue: 0.25),
                          title: "Top Tracer",         subtitle: "Đường từ trên màn hình đến địch", binding: $espTracer)
                    .disabled(!espMaster)
                rowDivider
                toggleRow(icon: "heart.fill",         iconColor: Color(red: 0.95, green: 0.28, blue: 0.28),
                          title: "Health Bar",         subtitle: "Thanh máu kẻ địch",        binding: $espHealth)
                    .disabled(!espMaster)
                rowDivider
                toggleRow(icon: "person.fill",        iconColor: Color(red: 0.70, green: 0.55, blue: 1.00),
                          title: "Player Name",        subtitle: "Tên kẻ địch",               binding: $espName)
                    .disabled(!espMaster)
                rowDivider
                toggleRow(icon: "ruler.fill",         iconColor: Color(red: 0.35, green: 0.85, blue: 0.85),
                          title: "Distance",           subtitle: "Khoảng cách đến địch",      binding: $espDistance)
                    .disabled(!espMaster)
                rowDivider
                toggleRow(icon: "gun",                iconColor: Color(red: 1.00, green: 0.85, blue: 0.10),
                          title: "Weapon ESP",         subtitle: "Tên súng đang cầm của địch", binding: $espWeapon)
                    .disabled(!espMaster)
            }
            .background(rowBg)
            .opacity(espMaster ? 1 : 0.55)
        }
    }

    // MARK: AIM section

    private var aimSection: some View {
        VStack(alignment: .leading, spacing: 6) {
            sectionHeader("AIM")
            VStack(spacing: 0) {

                // Silent aim
                toggleRow(icon: "scope",        iconColor: Color(red: 0.95, green: 0.30, blue: 0.30),
                          title: "Silent Aim",   subtitle: "Tự động nhắm vào địch",  binding: $silentAim)
                    .onChange(of: silentAim) { v in if v { aimSystem = false } }

                if silentAim {
                    rowDivider
                    // Target mode
                    pickerRow(icon: "dot.scope",          iconColor: Color(red: 0.95, green: 0.60, blue: 0.20),
                              title: "Target",             options: AimTargetMode.allCases.map(\.label),
                              selected: Binding(get: { aimTargetRaw },
                                                set: { aimTargetRaw = $0 }))
                    rowDivider
                    // Headshot rate
                    pickerRow(icon: "brain.head.profile",  iconColor: Color(red: 0.85, green: 0.40, blue: 0.95),
                              title: "Headshot Rate",       options: HeadRate.allCases.map(\.label),
                              selected: Binding(get: { headRateRaw },
                                                set: { headRateRaw = $0 }))
                    rowDivider
                    // FOV circle
                    toggleRow(icon: "circle.dashed",       iconColor: Color(red: 0.40, green: 0.82, blue: 1.00),
                              title: "Aim FOV Circle",      subtitle: "Vòng tròn FOV 100px",  binding: $aimFov)
                }

                rowDivider

                // Aim system (mutually exclusive with silent aim)
                toggleRow(icon: "target",       iconColor: Color(red: 0.45, green: 0.90, blue: 0.50),
                          title: "Aim System",   subtitle: "Lock collider tự động",  binding: $aimSystem)
                    .onChange(of: aimSystem) { v in if v { silentAim = false } }

                if aimSystem {
                    rowDivider
                    pickerRow(icon: "dot.crosshairs",   iconColor: Color(red: 0.50, green: 0.85, blue: 0.55),
                              title: "Aim Target",       options: AimSystemTarget.allCases.map(\.label),
                              selected: Binding(get: { aimSysTargetRaw },
                                                set: { aimSysTargetRaw = $0 }))
                }

                rowDivider

                toggleRow(icon: "waveform.slash", iconColor: Color(red: 1.00, green: 0.80, blue: 0.25),
                          title: "No Recoil",      subtitle: "Không giật súng",       binding: $noRecoil)
            }
            .background(rowBg)
        }
    }

    // MARK: SETTINGS section

    private var settingsSection: some View {
        VStack(alignment: .leading, spacing: 6) {
            sectionHeader("SETTINGS")
            VStack(spacing: 0) {
                toggleRow(icon: "wind",        iconColor: Color(red: 0.40, green: 0.75, blue: 1.00),
                          title: "Fast Parachute", subtitle: "Dù nhanh xuống đất",   binding: $fastParachute)
                rowDivider

                // Speed running with level picker
                toggleRow(icon: "figure.run",  iconColor: Color(red: 0.40, green: 0.88, blue: 0.55),
                          title: "Speed Running",  subtitle: "Chạy nhanh hơn bình thường", binding: $speedRunning)
                if speedRunning {
                    rowDivider
                    pickerRow(icon: "speedometer",   iconColor: Color(red: 0.40, green: 0.88, blue: 0.55),
                              title: "Tốc độ",        options: SpeedLevel.allCases.map(\.label),
                              selected: Binding(get: { speedLevelRaw },
                                                set: { speedLevelRaw = $0 }))
                }
            }
            .background(rowBg)
        }
    }

    // MARK: - Log content

    private var logContent: some View {
        VStack(spacing: 0) {
            ScrollViewReader { proxy in
                ScrollView {
                    VStack(alignment: .leading, spacing: 4) {
                        if logLines.isEmpty {
                            Text("Chưa có log. Bấm Inject Cheat để bắt đầu.")
                                .font(.system(size: 13, design: .monospaced))
                                .foregroundStyle(Color(white: 0.45))
                                .padding(.top, 20)
                                .frame(maxWidth: .infinity)
                        } else {
                            ForEach(Array(logLines.enumerated()), id: \.offset) { idx, line in
                                Text(line)
                                    .font(.system(size: 13, design: .monospaced))
                                    .foregroundStyle(
                                        line.hasPrefix("✅") ? Color(red: 0.25, green: 0.85, blue: 0.45) :
                                        line.hasPrefix("❌") ? Color(red: 0.95, green: 0.30, blue: 0.30) :
                                        Color(white: 0.75)
                                    )
                                    .id(idx)
                            }
                        }
                        Spacer(minLength: 16)
                    }
                    .padding(14)
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
                .onChange(of: logLines.count) { _ in
                    if let last = logLines.indices.last {
                        withAnimation { proxy.scrollTo(last, anchor: .bottom) }
                    }
                }
            }

            // "Xem Files" button — hiện sau khi inject thành công
            if injectDone, injectedDocsPath != nil {
                Button { showFileBrowser = true } label: {
                    HStack(spacing: 8) {
                        Image(systemName: "folder.fill")
                            .font(.system(size: 15, weight: .semibold))
                        Text("Xem Documents/")
                            .font(.system(size: 15, weight: .semibold))
                        Spacer()
                        Image(systemName: "chevron.right")
                            .font(.system(size: 13, weight: .semibold))
                            .foregroundStyle(Color(white: 0.40))
                    }
                    .foregroundStyle(Color(red: 0.25, green: 0.85, blue: 0.45))
                    .padding(.horizontal, 16)
                    .padding(.vertical, 13)
                    .background(
                        Color(red: 0.10, green: 0.20, blue: 0.13),
                        in: RoundedRectangle(cornerRadius: 12, style: .continuous)
                    )
                    .overlay(
                        RoundedRectangle(cornerRadius: 12, style: .continuous)
                            .strokeBorder(Color(red: 0.25, green: 0.85, blue: 0.45).opacity(0.35),
                                          lineWidth: 1)
                    )
                }
                .buttonStyle(.plain)
                .padding(.horizontal, 14)
                .padding(.bottom, 10)
            }

            Spacer(minLength: 96) // clearance for inject bar
        }
        .sheet(isPresented: $showFileBrowser) {
            if let docsPath = injectedDocsPath,
               let containerPath = (docsPath as NSString).deletingLastPathComponent
                   .isEmpty ? nil : (docsPath as NSString).deletingLastPathComponent as String {
                NavigationView {
                    FileBrowserView(
                        containerPath: containerPath,
                        startPath: docsPath,
                        title: "Documents",
                        bundleID: injectedBundleID
                    )
                    .navigationBarTitleDisplayMode(.inline)
                    .toolbar {
                        ToolbarItem(placement: .cancellationAction) {
                            Button("Đóng") { showFileBrowser = false }
                        }
                    }
                }
                .preferredColorScheme(.dark)
            }
        }
    }

    // MARK: - Inject bar

    private var injectBar: some View {
        HStack(spacing: 10) {
            // Inject button
            Button {
                runInject()
            } label: {
                HStack(spacing: 8) {
                    if isInjecting {
                        ProgressView().tint(Color(red: 0.12, green: 0.12, blue: 0.14))
                            .scaleEffect(0.85)
                    } else {
                        Image(systemName: injectDone ? "checkmark.circle.fill" : "arrow.down.circle.fill")
                            .font(.system(size: 18, weight: .semibold))
                    }
                    Text(isInjecting ? "Đang inject…" : injectDone ? "Inject lại" : "Inject Cheat")
                        .font(.system(size: 17, weight: .bold))
                }
                .foregroundStyle(Color(red: 0.12, green: 0.12, blue: 0.14))
                .frame(maxWidth: .infinity)
                .padding(.vertical, 16)
                .background(
                    Color(red: 0.93, green: 0.91, blue: 0.84),
                    in: RoundedRectangle(cornerRadius: 14, style: .continuous)
                )
            }
            .buttonStyle(.plain)
            .disabled(isInjecting)

            // Reset button
            Button {
                resetDefaults()
            } label: {
                Image(systemName: "arrow.counterclockwise")
                    .font(.system(size: 18, weight: .semibold))
                    .foregroundStyle(Color(white: 0.70))
                    .frame(width: 52, height: 52)
                    .background(
                        Color(red: 0.16, green: 0.16, blue: 0.18),
                        in: RoundedRectangle(cornerRadius: 14, style: .continuous)
                    )
            }
            .buttonStyle(.plain)
        }
        .padding(.horizontal, 14)
        .padding(.bottom, 24)
        .padding(.top, 10)
        .background(
            Color(red: 0.07, green: 0.07, blue: 0.08)
                .shadow(color: .black.opacity(0.6), radius: 12, y: -4)
        )
    }

    // MARK: - Actions

    private func runInject() {
        guard !isInjecting else { return }
        isInjecting = true
        injectDone  = false
        injectError = nil
        menuTab     = 1   // switch to Log tab
        let target  = game
        logLines    = []
        logLines.append("▶ Inject \(target.displayName) — \(formattedNow())")

        // Snapshot current toggle states into CheatConfig
        let config = CheatConfig(
            espMaster:     espMaster,
            espBox:        espBox,
            espTracer:     espTracer,
            espHealth:     espHealth,
            espName:       espName,
            espDistance:   espDistance,
            silentAim:     silentAim,
            aimSystem:     aimSystem,
            aimTargetMode: aimTargetRaw,
            headRateIndex: headRateRaw,
            aimFov:        aimFov,
            noRecoil:      noRecoil,
            aimSysTarget:  aimSysTargetRaw,
            fastParachute: fastParachute,
            speedRunning:  speedRunning,
            speedLevel:    speedLevelRaw
        )

        let capturedBundleID = target.rawValue
        DispatchQueue.global(qos: .userInitiated).async {
            var success = false
            var docsPath: String? = nil
            do {
                docsPath = try CheatInjectService.inject(into: target, config: config) { line in
                    DispatchQueue.main.async { logLines.append(line) }
                }
                success = true
            } catch {
                DispatchQueue.main.async {
                    logLines.append("❌ \(error.localizedDescription)")
                }
            }
            DispatchQueue.main.async {
                isInjecting = false
                injectDone  = success
                if success, let dp = docsPath {
                    injectedDocsPath = dp
                    injectedBundleID = capturedBundleID
                }
                logLines.append(success ? "─── Hoàn tất ───" : "─── Thất bại ───")
            }
        }
    }

    private func resetDefaults() {
        espMaster = true; espBox = true; espTracer = true
        espHealth = true; espName = true; espDistance = true
        silentAim = false; aimSystem = false
        aimTargetRaw = AimTargetMode.mixed.rawValue
        headRateRaw  = HeadRate.r75.rawValue
        aimFov = false; noRecoil = false
        aimSysTargetRaw = AimSystemTarget.neck.rawValue
        fastParachute = false; speedRunning = false
        speedLevelRaw = SpeedLevel.lv2.rawValue
    }

    private func formattedNow() -> String {
        let f = DateFormatter()
        f.dateFormat = "HH:mm:ss"
        return f.string(from: Date())
    }

    // MARK: - Reusable row builders

    @ViewBuilder
    private func toggleRow(
        icon: String, iconColor: Color,
        title: String, subtitle: String,
        binding: Binding<Bool>
    ) -> some View {
        HStack(spacing: 14) {
            iconBox(systemName: icon, color: iconColor)
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                    .font(.system(size: 16, weight: .semibold))
                    .foregroundStyle(.white)
                Text(subtitle)
                    .font(.system(size: 13))
                    .foregroundStyle(Color(white: 0.50))
            }
            Spacer()
            Toggle("", isOn: binding)
                .labelsHidden()
                .tint(Color(red: 0.28, green: 0.72, blue: 0.45))
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 12)
    }

    @ViewBuilder
    private func pickerRow(
        icon: String, iconColor: Color,
        title: String,
        options: [String],
        selected: Binding<Int>
    ) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 14) {
                iconBox(systemName: icon, color: iconColor)
                Text(title)
                    .font(.system(size: 16, weight: .semibold))
                    .foregroundStyle(.white)
                Spacer()
                Text(options[safe: selected.wrappedValue] ?? "")
                    .font(.system(size: 13, weight: .medium))
                    .foregroundStyle(Color(white: 0.55))
            }
            HStack(spacing: 0) {
                ForEach(Array(options.enumerated()), id: \.offset) { idx, label in
                    Button { selected.wrappedValue = idx } label: {
                        Text(label)
                            .font(.system(size: 13, weight: .semibold))
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 7)
                            .background(
                                selected.wrappedValue == idx
                                    ? Color(red: 0.26, green: 0.26, blue: 0.30)
                                    : Color.clear
                            )
                            .foregroundStyle(
                                selected.wrappedValue == idx ? .white : Color(white: 0.45)
                            )
                    }
                    .buttonStyle(.plain)
                    if idx < options.count - 1 {
                        Rectangle()
                            .fill(Color(white: 0.20))
                            .frame(width: 0.5)
                            .frame(maxHeight: 28)
                    }
                }
            }
            .background(Color(red: 0.14, green: 0.14, blue: 0.16),
                        in: RoundedRectangle(cornerRadius: 8))
            .padding(.leading, 58)
            .padding(.trailing, 14)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
    }

    @ViewBuilder
    private func iconBox(systemName: String, color: Color) -> some View {
        ZStack {
            RoundedRectangle(cornerRadius: 10, style: .continuous)
                .fill(Color(red: 0.16, green: 0.16, blue: 0.18))
                .frame(width: 44, height: 44)
            Image(systemName: systemName)
                .font(.system(size: 19, weight: .semibold))
                .foregroundStyle(color)
        }
    }

    private func sectionHeader(_ text: String) -> some View {
        Text(text)
            .font(.system(size: 12, weight: .semibold))
            .foregroundStyle(Color(white: 0.45))
            .kerning15(0.8)
            .padding(.leading, 4)
    }

    private var rowDivider: some View {
        Rectangle()
            .fill(Color(white: 0.14))
            .frame(height: 0.5)
            .padding(.leading, 72)
    }

    private var rowBg: some View {
        RoundedRectangle(cornerRadius: 14, style: .continuous)
            .fill(Color(red: 0.12, green: 0.12, blue: 0.14))
    }
}

// MARK: - Safe subscript helper

private extension Array {
    subscript(safe index: Int) -> Element? {
        indices.contains(index) ? self[index] : nil
    }
}
