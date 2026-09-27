import AVFoundation
import SwiftUI
import DeskPairShared

/// Reads a pairing code off a desktop's screen.
///
/// AVFoundation rather than a library: iOS has decoded QR codes in the capture pipeline since iOS 7, so
/// this is a metadata output and forty lines of delegate. Nothing is bundled and nothing is downloaded.
struct QrScannerView: View {
    /// Called once, with what was read, or nil if the user backed out.
    let onResult: (ConnectLinkScanned?) -> Void

    @State private var denied = false

    /// A simulator has no camera at all. Without this the view would sit on a black rectangle for ever,
    /// which looks exactly like a scanner that is about to work.
    private var hasCamera: Bool { AVCaptureDevice.default(for: .video) != nil }

    var body: some View {
        NavigationStack {
            ZStack {
                if !hasCamera {
                    ContentUnavailableView(
                        String(localized: "scan.unavailable"),
                        systemImage: "camera.fill"
                    )
                } else if denied {
                    ContentUnavailableView(
                        String(localized: "scan.cameraDenied"),
                        systemImage: "camera.fill",
                        description: Text("scan.cameraDeniedHint")
                    )
                } else {
                    CameraPreview { value in
                        onResult(ConnectLink.companion.parse(text: value))
                    }
                    .ignoresSafeArea()

                    // A frame to aim with. Without it people hold the phone too close and the code fills
                    // the sensor, which is the one distance a scanner cannot read.
                    RoundedRectangle(cornerRadius: 16)
                        .strokeBorder(.white.opacity(0.9), lineWidth: 3)
                        .frame(width: 240, height: 240)
                        .shadow(radius: 8)
                }
            }
            .navigationTitle(Text("home.scan"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(String(localized: "connect.cancel")) { onResult(nil) }
                }
            }
            .task {
                guard hasCamera else { return }
                if AVCaptureDevice.authorizationStatus(for: .video) == .notDetermined {
                    _ = await AVCaptureDevice.requestAccess(for: .video)
                }
                denied = AVCaptureDevice.authorizationStatus(for: .video) != .authorized
            }
        }
    }
}

/// The capture session, wrapped so SwiftUI can hold it.
private struct CameraPreview: UIViewControllerRepresentable {
    let onCode: (String) -> Void

    func makeUIViewController(context: Context) -> ScannerController {
        let controller = ScannerController()
        controller.onCode = onCode
        return controller
    }

    func updateUIViewController(_ controller: ScannerController, context: Context) {}
}

final class ScannerController: UIViewController, AVCaptureMetadataOutputObjectsDelegate {
    var onCode: ((String) -> Void)?

    private let session = AVCaptureSession()
    private var preview: AVCaptureVideoPreviewLayer?

    /// One result only. A camera pointed at a code reads it many times a second, and a view that reported
    /// every one of them would push the same screen onto the stack until something broke.
    private var reported = false

    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = .black

        guard let device = AVCaptureDevice.default(for: .video),
              let input = try? AVCaptureDeviceInput(device: device),
              session.canAddInput(input) else {
            return
        }

        session.addInput(input)

        let output = AVCaptureMetadataOutput()
        guard session.canAddOutput(output) else { return }
        session.addOutput(output)

        output.setMetadataObjectsDelegate(self, queue: .main)
        // Set after the output is attached: the available types are empty until then, and assigning an
        // unavailable type throws.
        output.metadataObjectTypes = [.qr]

        let layer = AVCaptureVideoPreviewLayer(session: session)
        layer.videoGravity = .resizeAspectFill
        layer.frame = view.bounds
        view.layer.addSublayer(layer)
        preview = layer
    }

    override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        preview?.frame = view.bounds
    }

    override func viewWillAppear(_ animated: Bool) {
        super.viewWillAppear(animated)
        guard !session.isRunning else { return }
        // startRunning blocks while the device warms up, which on the main thread is a visible stall.
        DispatchQueue.global(qos: .userInitiated).async { [session] in session.startRunning() }
    }

    override func viewWillDisappear(_ animated: Bool) {
        super.viewWillDisappear(animated)
        session.stopRunning()
    }

    func metadataOutput(
        _ output: AVCaptureMetadataOutput,
        didOutput metadataObjects: [AVMetadataObject],
        from connection: AVCaptureConnection
    ) {
        guard !reported,
              let object = metadataObjects.first as? AVMetadataMachineReadableCodeObject,
              let value = object.stringValue else {
            return
        }

        reported = true
        UINotificationFeedbackGenerator().notificationOccurred(.success)
        onCode?(value)
    }
}

/// What a scan means for the app, in one place so the view and the model agree.
enum ScanOutcome {
    case cancelled
    case applied(target: String, message: String)
    case rejected(message: String)
}

/// Turns a scan into settings and a sentence.
///
/// The server and key go straight into settings — that is the whole reason the code carries them, and
/// asking someone to confirm a public key they cannot read would be theatre. The id is returned for the
/// caller to dial.
///
/// The one-time password is put aside for the next connection to this desk rather than stored. It is good
/// for one connection and the host replaces it the moment it is used, so remembering it would only mean
/// offering a dead password later on.
///
/// A free function rather than an extension because Swift cannot extend an optional of a protocol, and a
/// Kotlin enum arrives as a class rather than a Swift enum, so these are identity comparisons rather than
/// a switch.
@MainActor
func applyScan(_ scanned: ConnectLinkScanned?, to store: AppStore) -> ScanOutcome {
    guard let scanned else { return .cancelled }

    if let understood = scanned as? ConnectLinkScannedUnderstood {
        let link = understood.link

        // For this desk only, never into the settings: see AppStore.rememberScannedNetwork.
        store.rememberScannedNetwork(
            link.id,
            rendezvousServer: link.rendezvousServer,
            serverPublicKeyBase64: link.serverPublicKeyBase64
        )

        if !link.password.isEmpty {
            store.rememberScannedPassword(link.id, link.password)
        }

        let named = link.deviceName.isEmpty ? Targets.shared.formatId(id: link.id) : link.deviceName
        return .applied(target: link.id, message: String(format: String(localized: "scan.applied"), named))
    }

    guard let rejected = scanned as? ConnectLinkScannedRejected else {
        return .rejected(message: String(localized: "scan.notDeskPair"))
    }

    let problem = rejected.problem
    if problem == ConnectLink.Problem.newerVersion {
        return .rejected(
            message: String(format: String(localized: "scan.newerVersion"), Int(rejected.version)))
    }
    if problem == ConnectLink.Problem.noVersion {
        return .rejected(message: String(localized: "scan.noVersion"))
    }
    if problem == ConnectLink.Problem.noId {
        return .rejected(message: String(localized: "scan.noId"))
    }
    if problem == ConnectLink.Problem.damagedKey {
        return .rejected(message: String(localized: "scan.damagedKey"))
    }

    return .rejected(message: String(localized: "scan.notDeskPair"))
}
