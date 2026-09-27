import Foundation
import DeskPairShared

/// Secrets in the Keychain, as generic passwords under one service.
///
/// This is the same shape `MacSecretStore` uses on the desktop — a generic password whose account name is
/// the caller's key — so the two halves of the product store their small secrets the same way rather than
/// each inventing something.
///
/// What lives here is pinned fingerprints and, if the user asks, passwords. A fingerprint is not secret;
/// it is kept here because it has to be *unforgeable*. Anyone who can rewrite the pin file silently
/// defeats trust-on-first-use, and the Keychain is the storage iOS actually defends.
final class KeychainSecretStore: NSObject, SecretStore {

    private let service = "com.sunllo.deskpair"

    func get(key: String) -> String? {
        var query = baseQuery(account: key)
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne

        var item: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess,
              let data = item as? Data else {
            return nil
        }

        return String(data: data, encoding: .utf8)
    }

    func set(key: String, value: String) {
        guard let data = value.data(using: .utf8) else { return }

        let query = baseQuery(account: key)
        let attributes: [String: Any] = [kSecValueData as String: data]

        // Update first, add if there was nothing: SecItemAdd on an existing account fails with
        // errSecDuplicateItem rather than replacing it.
        let updated = SecItemUpdate(query as CFDictionary, attributes as CFDictionary)
        if updated == errSecItemNotFound {
            var insert = query
            insert[kSecValueData as String] = data
            // After first unlock rather than when-unlocked: reconnecting from a notification must not
            // require the phone to be unlocked at that instant.
            insert[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock
            SecItemAdd(insert as CFDictionary, nil)
        }
    }

    func remove(key: String) {
        SecItemDelete(baseQuery(account: key) as CFDictionary)
    }

    func keys(prefix: String) -> [String] {
        var query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecReturnAttributes as String: true,
            kSecMatchLimit as String: kSecMatchLimitAll,
        ]
        query[kSecReturnData as String] = false

        var item: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess,
              let entries = item as? [[String: Any]] else {
            return []
        }

        return entries
            .compactMap { $0[kSecAttrAccount as String] as? String }
            .filter { $0.hasPrefix(prefix) }
    }

    private func baseQuery(account: String) -> [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
    }
}

/// The settings file, in Application Support.
///
/// Not UserDefaults: the shared module already owns the shape of the document, and keeping it as one JSON
/// file means the Android and iOS apps store the same thing in the same form.
final class FileSettingsStorage: NSObject, SettingsStorage {

    private let url: URL

    override init() {
        let directory = FileManager.default
            .urls(for: .applicationSupportDirectory, in: .userDomainMask)
            .first!
            .appendingPathComponent("Sunllo", isDirectory: true)

        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        url = directory.appendingPathComponent("settings.json")
        super.init()
    }

    /// Shown in the About section, so a support request can ask for it by name.
    var path: String { url.path }

    func read() -> String? {
        try? String(contentsOf: url, encoding: .utf8)
    }

    func write(text: String) {
        // Atomic, so a process killed mid-write leaves the previous settings rather than half the new ones.
        try? text.write(to: url, atomically: true, encoding: .utf8)
    }
}
