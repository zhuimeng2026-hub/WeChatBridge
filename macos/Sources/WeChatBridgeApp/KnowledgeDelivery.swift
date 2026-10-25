import WeChatBridgeCore
import Foundation

/// Writes a WeChat archive into a folder as Markdown plus a durable copy of
/// the original ZIP. Keeping the archive makes a parsing change or a future
/// converter able to rebuild the note without asking WeChat again.
///
/// Two destinations share this assembly: 「沉淀到 Obsidian」 lands the note in
/// a subfolder of the user's vault, and 「沉淀到文件夹」 lands it in a subfolder
/// of the folder the user picked — the same shape either way. The note shape —
/// `聊天名.md` plus `附件/` — is the same too.
enum KnowledgeDelivery {
    enum Failure: LocalizedError {
        case notConfigured
        case unreadableArchive

        var errorDescription: String? {
            switch self {
            case .notConfigured: return L10n.text("还没有选择 Obsidian 知识库文件夹。")
            case .unreadableArchive: return L10n.text("微信导出的文件无法读取，原始文件已保留。")
            }
        }
    }

    /// The Obsidian entry: the vault is what the user configured, and the note
    /// goes into a `律档通` (or named) subfolder of it.
    @discardableResult
    static func deliver(
        urls: [URL],
        vaultPath: String,
        subfolder: String,
        chatName: String?,
        sceneName: String?
    ) throws -> [URL] {
        let vault = URL(fileURLWithPath: vaultPath, isDirectory: true)
        try FolderDelivery.validateFolder(vault)

        let subfolderPath = DisplayName.subfolderPath(subfolder)
        let folderName = subfolderPath.isEmpty ? "律档通" : subfolderPath
        return try write(
            urls: urls,
            root: vault.appendingPathComponent(folderName, isDirectory: true),
            chatName: chatName,
            sceneName: sceneName
        )
    }

    /// The folder entry: the folder is what the user configured, and the note
    /// goes into a `律档通` (or named) subfolder of it — the same landing
    /// shape the Obsidian entry writes inside its vault.
    @discardableResult
    static func deliver(
        urls: [URL],
        folderPath: String,
        subfolder: String,
        chatName: String?,
        sceneName: String?
    ) throws -> [URL] {
        let folder = URL(fileURLWithPath: folderPath, isDirectory: true)
        try FolderDelivery.validateFolder(folder)

        let subfolderPath = DisplayName.subfolderPath(subfolder)
        let folderName = subfolderPath.isEmpty ? "律档通" : subfolderPath
        return try write(
            urls: urls,
            root: folder.appendingPathComponent(folderName, isDirectory: true),
            chatName: chatName,
            sceneName: sceneName
        )
    }

    /// The shared assembly: one `聊天名.md` (merged when a same-named note is
    /// already there, uniquely numbered otherwise) and `附件/` holding the
    /// original archive plus any media unpacked from it. `root` is created if
    /// absent; its nearest existing parent must already be writable.
    @discardableResult
    private static func write(
        urls: [URL],
        root: URL,
        chatName: String?,
        sceneName: String?
    ) throws -> [URL] {
        guard !urls.isEmpty else { throw Failure.unreadableArchive }
        let attachments = root.appendingPathComponent("附件", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: attachments, withIntermediateDirectories: true)

        var written: [URL] = []
        for url in urls {
            let isArchive = url.pathExtension.lowercased() == "zip"
            let data: Data
            if isArchive {
                data = try Data(contentsOf: url, options: .mappedIfSafe)
            } else {
                data = Data()
            }
            let candidateTranscript = isArchive ? (try? WeChatNativeArchive.transcript(data)) ?? nil : nil
            let transcript = candidateTranscript.flatMap { candidate in
                (candidate.path as NSString).lastPathComponent == "聊天记录.txt"
                    || !(candidate.records ?? []).isEmpty ? candidate : nil
            }
            let archivePath: String
            let media: [String: String]
            var extractionFailed = false
            if isArchive {
                let rawArchiveFolder = attachments.appendingPathComponent("原始归档", isDirectory: true)
                try FileManager.default.createDirectory(at: rawArchiveFolder, withIntermediateDirectories: true)
                guard let archive = try FolderDelivery.save([url], to: rawArchiveFolder, checkCancellation: {}).first else {
                    throw Failure.unreadableArchive
                }
                archivePath = "原始归档/\(archive.lastPathComponent)"
                let extraction = try extractMedia(from: data, transcript: transcript, to: attachments)
                media = extraction.attachments
                extractionFailed = extraction.failed
            } else {
                // Standalone files are ordinary attachments, never fed to the ZIP reader.
                let category = category(for: url.lastPathComponent)
                let categoryFolder = attachments.appendingPathComponent(category, isDirectory: true)
                try FileManager.default.createDirectory(at: categoryFolder, withIntermediateDirectories: true)
                guard let saved = try FolderDelivery.save([url], to: categoryFolder, checkCancellation: {}).first else {
                    throw Failure.unreadableArchive
                }
                archivePath = "\(category)/\(saved.lastPathComponent)"
                media = [url.path: "\(category)/\(saved.lastPathComponent)"]
            }

            let title = ObsidianNote.title(
                chatName: chatName,
                transcript: transcript,
                archiveName: url.lastPathComponent
            )
            let markdown = ObsidianNote.render(
                title: title,
                chatName: chatName,
                sceneName: sceneName,
                createdAt: Date(),
                transcript: transcript,
                archiveName: archivePath,
                attachments: media,
                extractionFailed: extractionFailed,
                hasArchive: isArchive
            )
            let preferred = root.appendingPathComponent(DisplayName.sanitize(title) + ".md")
            if let transcript,
               let existing = try? String(contentsOf: preferred, encoding: .utf8) {
                switch ObsidianNote.merge(
                    existingMarkdown: existing,
                    transcript: transcript,
                    attachments: media,
                    archiveName: archivePath,
                    chatName: chatName,
                    sceneName: sceneName,
                    mergedAt: Date(),
                    extractionFailed: extractionFailed
                ) {
                case .merged(let merged):
                    try Data(merged.utf8).write(to: preferred, options: .atomic)
                    written.append(preferred)
                    continue
                case .nothingNew:
                    written.append(preferred)
                    continue
                case .notApplicable:
                    break
                }
            }
            let note = uniqueURL(
                in: root,
                name: DisplayName.sanitize(title) + ".md"
            )
            try Data(markdown.utf8).write(to: note, options: .atomic)
            written.append(note)
        }
        return written
    }

    private struct ExtractionResult {
        let attachments: [String: String]
        let failed: Bool
    }

    private static func extractMedia(
        from data: Data,
        transcript: WeChatNativeArchive.Transcript?,
        to attachments: URL
    ) throws -> ExtractionResult {
        let staging = FileManager.default.temporaryDirectory
            .appendingPathComponent("wechatbridge-media-" + UUID().uuidString, isDirectory: true)
        var result: [String: String] = [:]
        do {
            try FileManager.default.createDirectory(at: staging, withIntermediateDirectories: true)
            defer { try? FileManager.default.removeItem(at: staging) }
            let names = try WeChatNativeArchive.extract(data, to: staging)
                .filter { $0 != transcript?.path }
            for name in names {
                let source = staging.appendingPathComponent(name)
                let category = category(for: name)
                let folder = attachments.appendingPathComponent(category, isDirectory: true)
                try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
                guard let destination = try FolderDelivery.save([source], to: folder, checkCancellation: {}).first else { continue }
                result[name] = "\(category)/\(destination.lastPathComponent)"
            }
            return ExtractionResult(attachments: result, failed: false)
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            return ExtractionResult(attachments: result, failed: true)
        }
    }

    private static func category(for name: String) -> String {
        let ext = (name as NSString).pathExtension.lowercased()
        let images = Set(["png", "jpg", "jpeg", "gif", "bmp", "webp", "tif", "tiff", "heic", "heif", "avif", "svg", "ico"])
        let word = Set(["doc", "docx", "docm", "dot", "dotx", "dotm", "rtf"])
        let sheets = Set(["xls", "xlsx", "xlsm", "xlsb", "xlt", "xltx", "xltm", "csv", "tsv", "ods", "et"])
        let video = Set(["mp4", "mov", "m4v", "avi", "mkv", "wmv", "flv", "webm", "mpg", "mpeg", "3gp", "ts"])
        if images.contains(ext) { return "图片" }
        if word.contains(ext) { return "Word文档" }
        if sheets.contains(ext) { return "电子表格" }
        if video.contains(ext) { return "视频" }
        return "其他"
    }

    private static func uniqueURL(in folder: URL, name: String) -> URL {
        let ext = (name as NSString).pathExtension
        let stem = (name as NSString).deletingPathExtension
        var number = 1
        while true {
            let suffix = number == 1 ? "" : " \(number)"
            let candidate = folder.appendingPathComponent("\(stem)\(suffix).\(ext)")
            if !FileManager.default.fileExists(atPath: candidate.path) { return candidate }
            number += 1
        }
    }
}
